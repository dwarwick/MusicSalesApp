using Hangfire;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// What happens when an alignment reports back.
///
/// <para>
/// The cross-account copy itself is stubbed - it needs two real storage accounts and is one of the
/// two things in this codebase unit tests genuinely cannot reach. Everything <em>around</em> it is
/// exercised here: the idempotency guard, the published/needs-review decision, the version bump, and
/// what a failure is and is not allowed to take away.
/// </para>
/// </summary>
[TestFixture]
public class LyricsAlignmentCompletionServiceTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IBlobContainerFactory> _containers = null!;
    private Mock<IStagingToMediaCopier> _copier = null!;
    private Mock<IAppSettingsService> _appSettings = null!;
    private RecordingNotifier _notifier = null!;
    private RecordingBackgroundJobClient _jobs = null!;
    private LyricsAlignmentCompletionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"lyrics-completion-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);

        // Constructed from a connection string, which touches no network on its own. The copier that
        // would actually use them is mocked, so these only have to exist.
        //
        // The retry policy is switched off deliberately, and it is not a micro-optimisation. The
        // service sweeps the attempt's staging folder on every terminal path, which really does
        // reach for the network; against a development connection string with no Azurite behind it
        // the SDK's default policy retries with exponential backoff before giving up. That is
        // swallowed - the sweep is best-effort by design - but it turned this file into a four
        // minute test run on its own. Failing fast makes the same code take the same path in
        // milliseconds.
        var failFast = new BlobClientOptions();
        failFast.Retry.MaxRetries = 0;
        failFast.Retry.NetworkTimeout = TimeSpan.FromMilliseconds(250);

        _containers = new Mock<IBlobContainerFactory>();
        _containers.Setup(f => f.GetUploadStagingContainer())
            .Returns(new BlobContainerClient("UseDevelopmentStorage=true", "staging", failFast));
        _containers.Setup(f => f.GetMediaContainer())
            .Returns(new BlobContainerClient("UseDevelopmentStorage=true", "media", failFast));

        _copier = new Mock<IStagingToMediaCopier>();
        _copier.Setup(c => c.CreateStagingReadSasQuery(It.IsAny<BlobContainerClient>())).Returns("sig=x");

        _appSettings = new Mock<IAppSettingsService>();
        _appSettings.Setup(s => s.GetLyricsConfidenceThresholdAsync()).ReturnsAsync(0.7d);

        _notifier = new RecordingNotifier();
        _jobs = new RecordingBackgroundJobClient();

        _service = new LyricsAlignmentCompletionService(
            _factory,
            _containers.Object,
            _jobs,
            _copier.Object,
            _appSettings.Object,
            _notifier,
            Mock.Of<ILogger<LyricsAlignmentCompletionService>>());
    }

    [Test]
    public async Task AConfidentResultLandsForReviewAndBumpsTheVersion()
    {
        // The blob paths never change, so the version is the only thing telling anything downstream
        // that the timings were replaced.
        //
        // Note the starting state: a song that was ALREADY published, re-aligned. It drops back to
        // NeedsReview, because the new timings are not the ones the creator approved - the approval
        // was for the file these have just overwritten.
        var jobId = await AddJobAsync();
        await AddLyricsAsync(SongLyricsStatus.Published, version: 4);

        await _service.CompleteAsync(GoodResult(jobId, confidence: 0.93d));

        await using var context = new AppDbContext(_options);
        var lyrics = await context.SongLyrics.SingleAsync();
        var job = await context.LyricsAlignmentJobs.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lyrics.Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
            Assert.That(lyrics.Version, Is.EqualTo(5));
            Assert.That(lyrics.Confidence, Is.EqualTo(0.93d).Within(0.001));
            Assert.That(lyrics.LastJobId, Is.EqualTo(jobId));
            Assert.That(job.Status, Is.EqualTo(LyricsAlignmentJobStatus.Completed));
            Assert.That(job.Step, Is.EqualTo(LyricsAlignmentStep.Completed));
        });
    }

    [Test]
    public async Task ALowConfidenceResultIsStoredButWithheldFromListeners()
    {
        var jobId = await AddJobAsync();
        await AddLyricsAsync(SongLyricsStatus.Pending);

        await _service.CompleteAsync(GoodResult(jobId, confidence: 0.35d));

        await using var context = new AppDbContext(_options);
        var lyrics = await context.SongLyrics.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lyrics.Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
            Assert.That(lyrics.TimingsBlobPath, Is.Not.Null, "The timings are kept, not discarded.");
            Assert.That(lyrics.Version, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AReplayedCallbackForACompletedAttemptIsANoOp()
    {
        // The Function retries its terminal callback on any non-2xx, and the reconciler can drive
        // this too. A second call must not produce a second set of timings.
        var jobId = await AddJobAsync(LyricsAlignmentJobStatus.Completed, LyricsAlignmentStep.Completed);
        await AddLyricsAsync(SongLyricsStatus.Published, version: 2);

        await _service.CompleteAsync(GoodResult(jobId, confidence: 0.95d));

        await using var context = new AppDbContext(_options);
        var lyrics = await context.SongLyrics.SingleAsync();

        Assert.That(lyrics.Version, Is.EqualTo(2), "A replay must not bump the version.");
        _copier.Verify(
            c => c.CopyAsync(
                It.IsAny<BlobContainerClient>(), It.IsAny<string>(),
                It.IsAny<BlobContainerClient>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task AReplayedCallbackForAFailedAttemptIsAlsoANoOp()
    {
        // Catching Failed as well as Completed is the part people delete as redundant. An attempt
        // the creator cancelled still gets its callback when the orchestration eventually finishes,
        // and that late arrival must not resurrect it and publish timings for a run nobody wanted.
        var jobId = await AddJobAsync(LyricsAlignmentJobStatus.Failed, LyricsAlignmentStep.Failed);
        await AddLyricsAsync(SongLyricsStatus.Failed);

        await _service.CompleteAsync(GoodResult(jobId, confidence: 0.99d));

        await using var context = new AppDbContext(_options);
        var lyrics = await context.SongLyrics.SingleAsync();
        var job = await context.LyricsAlignmentJobs.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lyrics.Status, Is.EqualTo(SongLyricsStatus.Failed));
            Assert.That(job.Status, Is.EqualTo(LyricsAlignmentJobStatus.Failed));
        });
    }

    [Test]
    public async Task ACallbackForAnUnknownAttemptIsIgnoredRatherThanThrowing()
    {
        // The song was deleted, which cascades its attempts away. Throwing would answer non-2xx,
        // which makes the Function retry a callback that can never be accepted - forever.
        Assert.DoesNotThrowAsync(() => _service.CompleteAsync(GoodResult(Guid.NewGuid(), 0.9d)));
    }

    [Test]
    public async Task AnUnusableOutcomeFailsTheAttemptWithTheReportedCode()
    {
        var jobId = await AddJobAsync();
        await AddLyricsAsync(SongLyricsStatus.Pending);

        await _service.CompleteAsync(new LyricsAlignmentResult
        {
            JobId = jobId,
            Outcome = LyricsAlignmentOutcome.Unusable,
            FailureCode = LyricsAlignmentFailureCodes.NoTokensMatched
        });

        await using var context = new AppDbContext(_options);
        var job = await context.LyricsAlignmentJobs.SingleAsync();
        var lyrics = await context.SongLyrics.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(job.Status, Is.EqualTo(LyricsAlignmentJobStatus.Failed));
            Assert.That(job.FailureCode, Is.EqualTo(LyricsAlignmentFailureCodes.NoTokensMatched));
            Assert.That(lyrics.Status, Is.EqualTo(SongLyricsStatus.Failed));
        });
    }

    [Test]
    public async Task AStructurallyBrokenResultFailsRatherThanNeedingReview()
    {
        // Non-monotonic timings are not "imprecise", they are unusable: a player binary-searching
        // them lands anywhere. There is nothing to review, so offering them as reviewable would
        // waste the creator's time.
        var jobId = await AddJobAsync();
        await AddLyricsAsync(SongLyricsStatus.Pending);

        var result = GoodResult(jobId, confidence: 0.95d);
        result.IsMonotonic = false;

        await _service.CompleteAsync(result);

        await using var context = new AppDbContext(_options);
        var job = await context.LyricsAlignmentJobs.SingleAsync();

        Assert.That(job.FailureCode, Is.EqualTo(LyricsAlignmentFailureCodes.TimingsNotMonotonic));
    }

    [Test]
    public async Task AFailedRerunDoesNotTakeAwayTimingsThatArePublished()
    {
        // The most important thing a failure must NOT do. A song serving good lyrics keeps serving
        // them; a re-run that failed is a re-run that failed.
        var jobId = await AddJobAsync();
        await AddLyricsAsync(SongLyricsStatus.Published, version: 3);

        await _service.FailAsync(jobId, LyricsAlignmentFailureCodes.SeparationFailed, "nope");

        await using var context = new AppDbContext(_options);
        var lyrics = await context.SongLyrics.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lyrics.Status, Is.EqualTo(SongLyricsStatus.Published));
            Assert.That(lyrics.TimingsBlobPath, Is.EqualTo("abc/abc-lyrics.json"));
            Assert.That(lyrics.Version, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task FailingAnAlreadyTerminalAttemptIsANoOp()
    {
        var jobId = await AddJobAsync(LyricsAlignmentJobStatus.Completed, LyricsAlignmentStep.Completed);

        await _service.FailAsync(jobId, LyricsAlignmentFailureCodes.Abandoned, "late sweep");

        await using var context = new AppDbContext(_options);
        var job = await context.LyricsAlignmentJobs.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(job.Status, Is.EqualTo(LyricsAlignmentJobStatus.Completed));
            Assert.That(job.FailureCode, Is.Null);
        });
    }

    [Test]
    public async Task TheOrchestrationRecordIsClosedOutAlongsideTheAttempt()
    {
        // Otherwise the reconciler keeps finding a task row that says Running for a job that
        // finished, and asks Azure about it on every sweep forever.
        var jobId = await AddJobAsync(durableTaskId: 1);
        await AddDurableTaskAsync();
        await AddLyricsAsync(SongLyricsStatus.Pending);

        await _service.CompleteAsync(GoodResult(jobId, confidence: 0.9d));

        await using var context = new AppDbContext(_options);
        var task = await context.DurableFunctionTasks.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(task.Status, Is.EqualTo(DurableTaskStatus.Completed));
            Assert.That(task.CompletedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task TheCreatorIsToldWhateverHappens()
    {
        var jobId = await AddJobAsync();
        await AddLyricsAsync(SongLyricsStatus.Pending);

        await _service.CompleteAsync(GoodResult(jobId, confidence: 0.9d));

        Assert.That(_notifier.Steps, Does.Contain(LyricsAlignmentStep.Completed));
    }

    private static LyricsAlignmentResult GoodResult(Guid jobId, double confidence) => new()
    {
        JobId = jobId,
        Outcome = LyricsAlignmentOutcome.Aligned,
        TimingsBlobPath = MediaProcessingStagingPaths.LyricsTimings(jobId),
        LrcBlobPath = MediaProcessingStagingPaths.LyricsLrc(jobId),
        Confidence = confidence,
        LyricTokenCount = 200,
        MatchedTokenCount = 190,
        LineCount = 30,
        LinesWithTimingCount = 30,
        IsMonotonic = true,
        DurationMs = 200_000,
        LastWordEndMs = 195_000
    };

    [Test]
    public async Task ASuccessfulRunQueuesExactlyOneCompletionEmail()
    {
        // The creator's only notification if they closed the tab - timing takes minutes, so closing
        // it is the expected behaviour rather than the exception.
        var jobId = await AddJobAsync();
        await AddLyricsAsync(SongLyricsStatus.Pending);

        await _service.CompleteAsync(GoodResult(jobId, confidence: 0.93d));

        Assert.That(_jobs.Created, Is.EqualTo(1));
    }

    [Test]
    public async Task AFailedRunQueuesTheEmailToo()
    {
        // A creator who is told nothing after a failure waits for something that is never coming.
        var jobId = await AddJobAsync();
        await AddLyricsAsync(SongLyricsStatus.Pending);

        await _service.FailAsync(jobId, LyricsAlignmentFailureCodes.AlignmentFailed, "nope");

        Assert.That(_jobs.Created, Is.EqualTo(1));
    }

    [Test]
    public async Task AReplayedCallbackDoesNotQueueASecondEmail()
    {
        // The Function retries its terminal callback on any non-2xx and the reconciler can drive the
        // same completion, so without the already-terminal guard a creator gets the same mail twice.
        var jobId = await AddJobAsync();
        await AddLyricsAsync(SongLyricsStatus.Pending);

        await _service.CompleteAsync(GoodResult(jobId, confidence: 0.93d));
        await _service.CompleteAsync(GoodResult(jobId, confidence: 0.93d));

        Assert.That(_jobs.Created, Is.EqualTo(1));
    }

    [Test]
    public async Task AnUnknownJobQueuesNothing()
    {
        await _service.CompleteAsync(GoodResult(Guid.NewGuid(), confidence: 0.9d));

        Assert.That(_jobs.Created, Is.Zero);
    }

    private async Task<Guid> AddJobAsync(
        LyricsAlignmentJobStatus status = LyricsAlignmentJobStatus.Processing,
        LyricsAlignmentStep step = LyricsAlignmentStep.WritingOutputs,
        int? durableTaskId = null)
    {
        var jobId = Guid.NewGuid();

        await using var context = new AppDbContext(_options);

        if (!await context.SongMetadata.AnyAsync())
        {
            context.SongMetadata.Add(new SongMetadata
            {
                Id = 1,
                SongTitle = "Night Drive",
                CreatorId = 7,
                MediaGuid = Guid.Parse("abc00000-0000-0000-0000-000000000000"),
                Mp3BlobPath = "abc/abc-music.mp3"
            });
        }

        context.LyricsAlignmentJobs.Add(new LyricsAlignmentJob
        {
            JobId = jobId,
            SongMetadataId = 1,
            CreatorId = 7,
            LyricsBlobPath = "abc/abc-lyrics.txt",
            DurableFunctionTaskId = durableTaskId,
            Status = status,
            Step = step,
            StepUpdatedAt = DateTime.UtcNow
        });

        await context.SaveChangesAsync();
        return jobId;
    }

    private async Task AddDurableTaskAsync()
    {
        await using var context = new AppDbContext(_options);
        context.DurableFunctionTasks.Add(new DurableFunctionTask
        {
            Id = 1,
            InstanceId = "instance-1",
            FunctionName = LyricsAlignmentInvoker.OrchestratorName,
            Status = DurableTaskStatus.Running,
            InputJson = "{}"
        });
        await context.SaveChangesAsync();
    }

    private async Task AddLyricsAsync(SongLyricsStatus status, int version = 0)
    {
        await using var context = new AppDbContext(_options);
        context.SongLyrics.Add(new SongLyrics
        {
            SongMetadataId = 1,
            LyricsBlobPath = "abc/abc-lyrics.txt",
            TimingsBlobPath = status == SongLyricsStatus.Pending ? null : "abc/abc-lyrics.json",
            Status = status,
            Version = version
        });
        await context.SaveChangesAsync();
    }

    private sealed class RecordingNotifier : ILyricsAlignmentNotifier
    {
        public List<LyricsAlignmentStep> Steps { get; } = [];

        public Task NotifyAsync(int c, LyricsAlignmentProgress p, CancellationToken t = default)
        {
            Steps.Add(p.Step);
            return Task.CompletedTask;
        }

        public Task NotifyStepAsync(
            int c, Guid j, LyricsAlignmentStep step, string detail = null, CancellationToken t = default)
        {
            Steps.Add(step);
            return Task.CompletedTask;
        }
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    /// <summary>Counts what was enqueued, so the email hook can be asserted without Hangfire.</summary>
    private sealed class RecordingBackgroundJobClient : IBackgroundJobClient
    {
        public int Created { get; private set; }

        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state)
        {
            Created++;
            return Guid.NewGuid().ToString("N");
        }

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) => true;
    }
}
