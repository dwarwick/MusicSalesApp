using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The API half of the pipeline: what happens when the Azure Function reports back.
///
/// <para>
/// The behaviours pinned here are the ones that a queue can break. The completion callback is
/// retried on any non-2xx and can also be replayed from the poison path, so it has to be safe to
/// call twice; and a failure verdict has to be recorded rather than retried, or a genuinely corrupt
/// upload would cycle forever.
/// </para>
/// </summary>
[TestFixture]
public class MediaProcessingCompletionServiceTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IBlobContainerFactory> _containers = null!;
    private Mock<ISongMetadataService> _metadata = null!;
    private Mock<IOpenGraphService> _openGraph = null!;
    private Mock<ISongUploadJobService> _jobService = null!;
    private RecordingProgressNotifier _progress = null!;
    private MediaProcessingCompletionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"completion-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);
        _containers = new Mock<IBlobContainerFactory>();
        _metadata = new Mock<ISongMetadataService>();
        _openGraph = new Mock<IOpenGraphService>();
        _jobService = new Mock<ISongUploadJobService>();
        _progress = new RecordingProgressNotifier();

        _service = new MediaProcessingCompletionService(
            _factory,
            _containers.Object,
            _metadata.Object,
            _openGraph.Object,
            _jobService.Object,
            _progress,
            Options.Create(new MediaProcessingOptions()),
            Mock.Of<ILogger<MediaProcessingCompletionService>>());
    }

    [Test]
    public async Task ReplayedCompletionForAFinishedJob_IsANoOp()
    {
        // The queue retries on any non-2xx, so a lost response means this arrives twice. Assembling
        // again would publish the same song a second time.
        var jobId = await AddJobAsync(SongUploadJobStatus.Completed, AudioProcessingStep.Completed);

        await _service.CompleteAsync(new AudioTranscodeResult
        {
            JobId = jobId,
            Outcome = AudioProcessingOutcome.Playable,
            DurationSeconds = 10
        });

        _metadata.Verify(
            service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()),
            Times.Never);
        _containers.Verify(factory => factory.GetUploadStagingContainer(), Times.Never);
    }

    [Test]
    public async Task CompletionForAnUnknownJob_IsIgnoredRatherThanThrowing()
    {
        // Throwing would make the Function retry a message that can never succeed, all the way to
        // the poison queue.
        Assert.DoesNotThrowAsync(() => _service.CompleteAsync(new AudioTranscodeResult
        {
            JobId = Guid.NewGuid(),
            Outcome = AudioProcessingOutcome.Playable,
            DurationSeconds = 10
        }));

        await using var context = new AppDbContext(_options);
        Assert.That(context.SongUploadJobs.Count(), Is.Zero);
    }

    [Test]
    public async Task AnUnplayableVerdict_FailsTheJobAndTellsTheCreatorWhy()
    {
        var jobId = await AddJobAsync(SongUploadJobStatus.Processing, AudioProcessingStep.Analyzing);

        await _service.CompleteAsync(new AudioTranscodeResult
        {
            JobId = jobId,
            Outcome = AudioProcessingOutcome.Unplayable,
            FailureCode = MediaProcessingFailureCodes.NotDecodable,
            Diagnostic = "Invalid data found when processing input"
        });

        await using var context = new AppDbContext(_options);
        var job = await context.SongUploadJobs.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(job.Status, Is.EqualTo(SongUploadJobStatus.Failed));
            Assert.That(job.Step, Is.EqualTo(AudioProcessingStep.Failed));
            Assert.That(job.FailureCode, Is.EqualTo(MediaProcessingFailureCodes.NotDecodable));
            Assert.That(job.FailureMessage, Does.Contain("Invalid data"));
            Assert.That(job.CompletedAt, Is.Not.Null);
        });

        // No song is created: SongMetadata only ever gets a row on success, which is why the
        // catalogue can never show a track with no playable audio.
        _metadata.Verify(
            service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()),
            Times.Never);
    }

    [Test]
    public async Task APlayableVerdictWithNoDuration_IsTreatedAsAFailure()
    {
        // Duration is what the player and the payout rules run on; publishing a song without one
        // would produce a track that cannot be scrubbed or credited.
        var jobId = await AddJobAsync(SongUploadJobStatus.Processing, AudioProcessingStep.Verifying);

        await _service.CompleteAsync(new AudioTranscodeResult
        {
            JobId = jobId,
            Outcome = AudioProcessingOutcome.Playable,
            DurationSeconds = 0
        });

        await using var context = new AppDbContext(_options);
        var job = await context.SongUploadJobs.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(job.Status, Is.EqualTo(SongUploadJobStatus.Failed));
            Assert.That(job.FailureCode, Is.EqualTo(MediaProcessingFailureCodes.ZeroDuration));
        });
    }

    [Test]
    public async Task APlayableResultForAnAlreadyFailedJob_DoesNotAssembleItAnyway()
    {
        // The reconciler declares a job dead on a stale StepUpdatedAt, and that timestamp is only
        // refreshed by progress pings - which swallow their own failures, so a site restart makes a
        // healthy Function look stalled. When that Function then finishes and posts a Playable
        // result, assembling it would publish a song the creator has already been told failed.
        var jobId = await AddJobAsync(SongUploadJobStatus.Failed, AudioProcessingStep.Failed);

        await _service.CompleteAsync(new AudioTranscodeResult
        {
            JobId = jobId,
            Outcome = AudioProcessingOutcome.Playable,
            DurationSeconds = 210
        });

        _metadata.Verify(
            service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()),
            Times.Never);

        await using var context = new AppDbContext(_options);
        var job = await context.SongUploadJobs.SingleAsync();
        Assert.That(job.Status, Is.EqualTo(SongUploadJobStatus.Failed), "A failed job must stay failed.");
    }

    [Test]
    public async Task ALateResultForAFailedJob_CleansUpWhatTheFunctionLeftBehind()
    {
        // A retry that finished after the job was failed can recreate staging, and any cover-art
        // renditions went straight into the media container before the row existed. Staging has a
        // 7-day lifecycle rule; the media account has none and cannot safely be given one.
        var jobId = await AddJobAsync(SongUploadJobStatus.Failed, AudioProcessingStep.Failed, ".jpg");

        await _service.CompleteAsync(new AudioTranscodeResult
        {
            JobId = jobId,
            Outcome = AudioProcessingOutcome.Playable,
            DurationSeconds = 210
        });

        Assert.Multiple(() =>
        {
            _jobService.Verify(
                service => service.DeleteStagedBlobsAsync(It.IsAny<SongUploadJob>(), It.IsAny<CancellationToken>()),
                Times.Once);

            // Reaching for the media container is the observable edge of the rendition sweep - the
            // deletes themselves go through a concrete Azure client this test cannot stand in for.
            _containers.Verify(factory => factory.GetMediaContainer(), Times.Once);
        });
    }

    [Test]
    public async Task ALateResultForAFailedAudioOnlyJob_DoesNotReachForTheMediaContainer()
    {
        // No cover art means no renditions to orphan, and no reason to touch the media account.
        var jobId = await AddJobAsync(SongUploadJobStatus.Failed, AudioProcessingStep.Failed);

        await _service.CompleteAsync(new AudioTranscodeResult
        {
            JobId = jobId,
            Outcome = AudioProcessingOutcome.Playable,
            DurationSeconds = 210
        });

        _containers.Verify(factory => factory.GetMediaContainer(), Times.Never);
    }

    [Test]
    public async Task AReplayedCompletionForAPublishedJob_DoesNotTouchItsRenditions()
    {
        // The same guard covers Completed, where the renditions are live and referenced. Deleting
        // them here would strip artwork off a perfectly good song.
        var jobId = await AddJobAsync(SongUploadJobStatus.Completed, AudioProcessingStep.Completed, ".jpg");

        await _service.CompleteAsync(new AudioTranscodeResult
        {
            JobId = jobId,
            Outcome = AudioProcessingOutcome.Playable,
            DurationSeconds = 210
        });

        // Cover art is present, so the sweep would run if the guard did not distinguish Completed
        // from Failed - and it would strip the artwork off a perfectly good song.
        _containers.Verify(factory => factory.GetMediaContainer(), Times.Never);
    }

    [Test]
    public async Task FailingAJob_LeavesTheBarWhereItGotToRatherThanResetting()
    {
        var jobId = await AddJobAsync(SongUploadJobStatus.Processing, AudioProcessingStep.Transcoding);

        await _service.FailAsync(jobId, MediaProcessingFailureCodes.TranscodeFailed, "FFmpeg gave up.");

        var update = _progress.Updates.Single();
        Assert.Multiple(() =>
        {
            Assert.That(update.Step, Is.EqualTo(AudioProcessingStep.Failed));
            Assert.That(
                update.OverallPercent,
                Is.EqualTo(AudioProcessingProgressCalculator.ToOverallPercent(AudioProcessingStep.Transcoding)),
                "The creator should see how far the song got, not a bar snapped to 0 or 100.");
            Assert.That(update.Detail, Does.Contain("FFmpeg gave up"));
        });
    }

    [Test]
    public async Task FailingAnAlreadyCompletedJob_DoesNotUnpublishIt()
    {
        // A late failure callback - say the reconciler and a real result racing - must never
        // retract a song that is already live.
        var jobId = await AddJobAsync(SongUploadJobStatus.Completed, AudioProcessingStep.Completed);

        await _service.FailAsync(jobId, MediaProcessingFailureCodes.Abandoned, "Timed out.");

        await using var context = new AppDbContext(_options);
        var job = await context.SongUploadJobs.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(job.Status, Is.EqualTo(SongUploadJobStatus.Completed));
            Assert.That(job.FailureCode, Is.Null);
        });
    }

    [Test]
    public async Task FailingAJob_CleansUpItsStagedBlobs()
    {
        var jobId = await AddJobAsync(SongUploadJobStatus.Processing, AudioProcessingStep.Downloading);

        await _service.FailAsync(jobId, MediaProcessingFailureCodes.NotDecodable, "Bad file.");

        _jobService.Verify(
            service => service.DeleteStagedBlobsAsync(
                It.Is<SongUploadJob>(job => job.MediaGuid == jobId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task FailureDiagnostics_AreTruncatedToTheirColumnWidths()
    {
        var jobId = await AddJobAsync(SongUploadJobStatus.Processing, AudioProcessingStep.Analyzing);

        await _service.FailAsync(jobId, new string('c', 500), new string('d', 5000));

        await using var context = new AppDbContext(_options);
        var job = await context.SongUploadJobs.SingleAsync();
        Assert.Multiple(() =>
        {
            // nvarchar(100) and nvarchar(2000); anything longer throws on save and would lose the
            // failure entirely.
            Assert.That(job.FailureCode, Has.Length.EqualTo(100));
            Assert.That(job.FailureMessage, Has.Length.EqualTo(2000));
        });
    }

    /// <param name="coverArtExtension">
    /// Set it to give the job cover art. Without it the rendition sweep short circuits at its first
    /// guard, so any test asserting on that path would pass without exercising it.
    /// </param>
    private async Task<Guid> AddJobAsync(
        SongUploadJobStatus status,
        AudioProcessingStep step,
        string coverArtExtension = null)
    {
        var jobId = Guid.NewGuid();
        await using var context = new AppDbContext(_options);
        context.SongUploadJobs.Add(new SongUploadJob
        {
            MediaGuid = jobId,
            CreatorId = 1,
            SongTitle = "Song",
            SourceBlobPath = MediaProcessingStagingPaths.Source(jobId, ".wav"),
            SourceFileName = "Song.wav",
            SourceExtension = ".wav",
            SourceContentType = "audio/wav",
            SourceFileSize = 1024,
            CoverArtBlobPath = coverArtExtension is null
                ? null
                : MediaProcessingStagingPaths.Cover(jobId, coverArtExtension),
            CoverArtFileName = coverArtExtension is null ? null : $"Song{coverArtExtension}",
            CoverArtExtension = coverArtExtension,
            Status = status,
            Step = step,
            StepUpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return jobId;
    }

    private sealed class RecordingProgressNotifier : IUploadProgressNotifier
    {
        public List<AudioProcessingProgress> Updates { get; } = [];

        public Task NotifyAsync(
            int creatorId,
            AudioProcessingProgress progress,
            CancellationToken cancellationToken = default)
        {
            Updates.Add(progress);
            return Task.CompletedTask;
        }

        public Task NotifyStepAsync(
            int creatorId,
            Guid jobId,
            AudioProcessingStep step,
            string detail = null,
            CancellationToken cancellationToken = default)
            => NotifyAsync(
                creatorId,
                new AudioProcessingProgress
                {
                    JobId = jobId,
                    Step = step,
                    OverallPercent = AudioProcessingProgressCalculator.ToOverallPercent(step),
                    Detail = detail
                },
                cancellationToken);
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
