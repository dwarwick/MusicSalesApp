using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Controllers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

/// <summary>
/// The progress endpoint, which is the only thing standing between an unreliable fire-and-forget
/// stream of HTTP posts and a progress bar the creator is watching.
///
/// <para>
/// Two properties matter more than anything else here, and both are about what the endpoint
/// <em>refuses</em> to do: it never lets the bar run backwards, and it never writes to the database
/// for a mere percentage tick.
/// </para>
/// </summary>
[TestFixture]
public class MediaProcessingControllerTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private RecordingProgressNotifier _progress = null!;
    private MediaProcessingController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"media-processing-controller-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);
        _progress = new RecordingProgressNotifier();

        _controller = new MediaProcessingController(
            Mock.Of<IMediaProcessingCompletionService>(),
            Mock.Of<IAudioProbeResultHandler>(),
            _progress,
            _factory,
            Mock.Of<ILogger<MediaProcessingController>>());
    }

    [Test]
    public async Task Progress_AdvancingAStep_PersistsItAndBroadcasts()
    {
        var jobId = await AddJobAsync(AudioProcessingStep.Downloading);

        var result = await _controller.Progress(
            NewProgress(jobId, AudioProcessingStep.Transcoding),
            CancellationToken.None);

        await using var context = new AppDbContext(_options);
        var job = await context.SongUploadJobs.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkResult>());
            Assert.That(job.Step, Is.EqualTo(AudioProcessingStep.Transcoding));
            Assert.That(job.Status, Is.EqualTo(SongUploadJobStatus.Processing));
            Assert.That(_progress.Updates, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Progress_WithAnEarlierStep_IsDroppedSoTheBarCannotRunBackwards()
    {
        // Queue retries and racing posts both replay earlier steps. Accepting one would visibly
        // rewind the creator's progress bar.
        var jobId = await AddJobAsync(AudioProcessingStep.Uploading);

        var result = await _controller.Progress(
            NewProgress(jobId, AudioProcessingStep.Downloading),
            CancellationToken.None);

        await using var context = new AppDbContext(_options);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkResult>(), "A stale update is not an error.");
            Assert.That(
                context.SongUploadJobs.Single().Step,
                Is.EqualTo(AudioProcessingStep.Uploading));
            Assert.That(_progress.Updates, Is.Empty, "A stale update must not reach the client either.");
        });
    }

    [Test]
    public async Task Progress_WithinTheSameStep_BroadcastsWithoutWritingToTheDatabase()
    {
        // Percentage ticks arrive far more often than step transitions. Persisting each one would
        // mean hundreds of writes per song against shared-hosting SQL for a value nobody reads back.
        var jobId = await AddJobAsync(AudioProcessingStep.Transcoding, TimeSpan.FromSeconds(5));
        DateTime before;
        await using (var context = new AppDbContext(_options))
        {
            before = (await context.SongUploadJobs.SingleAsync()).StepUpdatedAt;
        }

        var progress = NewProgress(jobId, AudioProcessingStep.Transcoding);
        progress.StepPercent = 42;
        await _controller.Progress(progress, CancellationToken.None);

        await using var verify = new AppDbContext(_options);
        var job = await verify.SongUploadJobs.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(job.StepUpdatedAt, Is.EqualTo(before), "Liveness was already fresh.");
            Assert.That(_progress.Updates, Has.Count.EqualTo(1));
            Assert.That(_progress.Updates[0].StepPercent, Is.EqualTo(42));
        });
    }

    [Test]
    public async Task Progress_WithinTheSameStep_RefreshesLivenessOnceItHasGoneStale()
    {
        // A ping proves the Function is still working whatever step it names. Without this, a
        // transcode that spends fifteen minutes inside one step would look frozen at the moment it
        // entered that step, and SongUploadJobReconciler would fail a job that is very much alive.
        var jobId = await AddJobAsync(AudioProcessingStep.Transcoding, TimeSpan.FromMinutes(5));
        DateTime before;
        await using (var context = new AppDbContext(_options))
        {
            before = (await context.SongUploadJobs.SingleAsync()).StepUpdatedAt;
        }

        await _controller.Progress(
            NewProgress(jobId, AudioProcessingStep.Transcoding),
            CancellationToken.None);

        await using var verify = new AppDbContext(_options);
        var job = await verify.SongUploadJobs.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(job.StepUpdatedAt, Is.GreaterThan(before));
            Assert.That(
                job.Step,
                Is.EqualTo(AudioProcessingStep.Transcoding),
                "Refreshing liveness must not be mistaken for advancing the step.");
        });
    }

    [Test]
    public async Task Progress_ReplayingAnEarlierStep_StillRefreshesStaleLiveness()
    {
        // The shape a queue retry actually takes: the second attempt starts again at Downloading
        // while the job row already says Uploading. None of those pings are advances, and treating
        // them as nothing at all let the reconciler reap a job mid-retry and delete the staging its
        // next attempt needed.
        var jobId = await AddJobAsync(AudioProcessingStep.Uploading, TimeSpan.FromMinutes(5));
        DateTime before;
        await using (var context = new AppDbContext(_options))
        {
            before = (await context.SongUploadJobs.SingleAsync()).StepUpdatedAt;
        }

        await _controller.Progress(
            NewProgress(jobId, AudioProcessingStep.Downloading),
            CancellationToken.None);

        await using var verify = new AppDbContext(_options);
        var job = await verify.SongUploadJobs.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(job.StepUpdatedAt, Is.GreaterThan(before));
            Assert.That(job.Step, Is.EqualTo(AudioProcessingStep.Uploading), "The bar must not rewind.");
            Assert.That(_progress.Updates, Is.Empty, "...and the creator must not see the replay.");
        });
    }

    [TestCase(AudioProcessingStep.Completed)]
    [TestCase(AudioProcessingStep.Failed)]
    public async Task Progress_ForAFinishedJob_IsDropped(AudioProcessingStep terminal)
    {
        // A ping still in flight when the song finished would otherwise pull a completed bar back
        // to mid-pipeline.
        var jobId = await AddJobAsync(terminal);

        var result = await _controller.Progress(
            NewProgress(jobId, AudioProcessingStep.Copying),
            CancellationToken.None);

        await using var context = new AppDbContext(_options);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkResult>());
            Assert.That(context.SongUploadJobs.Single().Step, Is.EqualTo(terminal));
            Assert.That(_progress.Updates, Is.Empty);
        });
    }

    [Test]
    public async Task Progress_ForAnUnknownJob_ReturnsOkRatherThanCausingARetry()
    {
        // Anything but 2xx makes the Function retry - and re-run a whole transcode over a cosmetic
        // update for a job that has already been cleaned up.
        var result = await _controller.Progress(
            NewProgress(Guid.NewGuid(), AudioProcessingStep.Transcoding),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkResult>());
            Assert.That(_progress.Updates, Is.Empty);
        });
    }

    [Test]
    public async Task Progress_WithoutAJobId_IsABadRequest()
    {
        var result = await _controller.Progress(
            new AudioProcessingProgress { Step = AudioProcessingStep.Transcoding },
            CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Complete_WithoutAJobId_IsABadRequest()
    {
        var result = await _controller.Complete(
            new AudioTranscodeResult { Outcome = AudioProcessingOutcome.Playable },
            CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ProbeResult_WithoutASongId_IsABadRequest()
    {
        var result = await _controller.ProbeResult(
            new AudioProbeResult { Kind = AudioProbeKind.TrackLengthRepair },
            CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    private static AudioProcessingProgress NewProgress(Guid jobId, AudioProcessingStep step)
        => new()
        {
            JobId = jobId,
            Step = step,
            OverallPercent = AudioProcessingProgressCalculator.ToOverallPercent(step)
        };

    private async Task<Guid> AddJobAsync(AudioProcessingStep step, TimeSpan? stepAge = null)
    {
        var jobId = Guid.NewGuid();
        await using var context = new AppDbContext(_options);
        context.SongUploadJobs.Add(new SongUploadJob
        {
            MediaGuid = jobId,
            CreatorId = 1,
            SongTitle = "Song",
            SourceFileName = "Song.wav",
            SourceExtension = ".wav",
            Step = step,
            Status = step is AudioProcessingStep.Completed
                ? SongUploadJobStatus.Completed
                : step is AudioProcessingStep.Failed
                    ? SongUploadJobStatus.Failed
                    : SongUploadJobStatus.Processing,
            StepUpdatedAt = DateTime.UtcNow - (stepAge ?? TimeSpan.FromSeconds(5))
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
            => NotifyAsync(creatorId, NewProgress(jobId, step), cancellationToken);
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
