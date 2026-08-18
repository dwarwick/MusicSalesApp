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
/// The Hangfire job that turns a submitted attempt into a running orchestration.
///
/// <para>
/// Almost everything here is about <b>not</b> starting one. Hangfire retries on any exception, and
/// the expensive failure mode is a start that succeeded but whose response was lost: retrying that
/// bills a second vocal-separation run - minutes of CPU - for a single attempt, and leaves two
/// orchestrations calling back about the same song, the second arriving after the first had already
/// published.
/// </para>
/// </summary>
[TestFixture]
public class LyricsAlignmentInvokerTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IDurableTaskClient> _durable = null!;
    private RecordingNotifier _notifier = null!;
    private Mock<ILyricsAlignmentCompletionService> _completion = null!;
    private LyricsAlignmentInvoker _invoker = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"lyrics-invoker-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);
        _durable = new Mock<IDurableTaskClient>();
        _notifier = new RecordingNotifier();
        _completion = new Mock<ILyricsAlignmentCompletionService>();

        _invoker = new LyricsAlignmentInvoker(
            _factory,
            _durable.Object,
            _notifier,
            _completion.Object,
            Mock.Of<ILogger<LyricsAlignmentInvoker>>());
    }

    [Test]
    public async Task AnAttemptThatAlreadyStartedIsNotStartedAgain()
    {
        // The retry guard, and the whole reason DurableFunctionTaskId is set in the same transaction
        // that records the start.
        var jobId = await AddJobAsync(durableTaskId: 17);

        await _invoker.InvokeAsync(jobId, context: null);

        _durable.Verify(
            client => client.StartAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<LyricsAlignmentRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "A retry after a successful start must not start a second orchestration.");
    }

    [Test]
    public async Task AnAttemptThatWasAlreadyCancelledIsNotStarted()
    {
        // The creator cancelled between submitting and Hangfire picking the job up. Starting now
        // would run a separation nobody is waiting for and bill for it.
        var jobId = await AddJobAsync(status: LyricsAlignmentJobStatus.Failed);

        await _invoker.InvokeAsync(jobId, context: null);

        _durable.Verify(
            client => client.StartAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<LyricsAlignmentRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task AnAttemptWhoseSongHasVanishedIsAQuietNoOp()
    {
        // Deleting a song cascades its attempts away. Throwing here would make Hangfire retry a job
        // that can never succeed, three times, for nothing.
        await _invoker.InvokeAsync(Guid.NewGuid(), context: null);

        _durable.Verify(
            client => client.StartAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<LyricsAlignmentRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ASuccessfulStartLinksTheOrchestrationAndAdvancesTheAttempt()
    {
        var jobId = await AddJobAsync();

        var task = new DurableFunctionTask
        {
            Id = 99,
            InstanceId = "instance-1",
            FunctionName = LyricsAlignmentInvoker.OrchestratorName
        };

        _durable
            .Setup(client => client.StartAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<LyricsAlignmentRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableStartOutcome(true, task, null));

        await _invoker.InvokeAsync(jobId, context: null);

        await using var context = new AppDbContext(_options);
        var job = await context.LyricsAlignmentJobs.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(job.DurableFunctionTaskId, Is.EqualTo(99));
            Assert.That(job.Status, Is.EqualTo(LyricsAlignmentJobStatus.Processing));
            Assert.That(job.Step, Is.EqualTo(LyricsAlignmentStep.Queued));
            Assert.That(_notifier.Steps, Does.Contain(LyricsAlignmentStep.Queued));
        });
    }

    [Test]
    public void AFailedStartWithRetriesRemainingThrowsSoHangfireTriesAgain()
    {
        // A Function app restarting is exactly what the Hangfire retry is for. Swallowing this would
        // turn a transient outage into a permanent failure the creator has to notice and redo.
        var jobId = AddJobAsync().GetAwaiter().GetResult();

        _durable
            .Setup(client => client.StartAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<LyricsAlignmentRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableStartOutcome(false, null, "Connection refused."));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _invoker.InvokeAsync(jobId, context: null));
    }

    [Test]
    public async Task AnAttemptForASongWithNoAudioFailsThroughTheSharedFunnelWithoutCallingAzure()
    {
        // This used to close the job itself, in a private copy of the completion service's failure
        // logic. Two consequences of that duplication: anything hooked into the real funnel - the
        // reconciler's view of the world, and now the creator's completion email - never saw this
        // failure at all, and the "don't demote already-published timings" guard existed twice and
        // could drift.
        var jobId = await AddJobAsync(withAudio: false);

        await _invoker.InvokeAsync(jobId, context: null);

        _completion.Verify(
            service => service.FailAsync(
                jobId,
                LyricsAlignmentFailureCodes.AudioBlobMissing,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _durable.Verify(
            client => client.StartAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<LyricsAlignmentRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public void AFailedStartWithRetriesRemainingDoesNotCloseTheJob()
    {
        // The counterpart to the delegation above. While Hangfire still has attempts left the
        // invoker throws rather than failing the job, so the funnel must stay untouched - closing it
        // here would turn a Function app restart into a permanent failure the creator has to redo.
        //
        // The opposite branch, StarterUnreachable on the final attempt, is not reachable from a test:
        // it is gated on Hangfire's RetryCount job parameter, and a PerformContext needs a real
        // JobStorage and IStorageConnection to construct. It is the same three-line delegation as the
        // no-audio path above.
        var jobId = AddJobAsync().GetAwaiter().GetResult();

        _durable
            .Setup(client => client.StartAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<LyricsAlignmentRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableStartOutcome(false, null, "Connection refused."));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _invoker.InvokeAsync(jobId, context: null));

        _completion.Verify(
            service => service.FailAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ASuccessfulStartNeverTouchesTheFailureFunnel()
    {
        var jobId = await AddJobAsync();
        _durable
            .Setup(client => client.StartAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<LyricsAlignmentRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableStartOutcome(
                true,
                new DurableFunctionTask { Id = 5, InstanceId = "abc" },
                null));

        await _invoker.InvokeAsync(jobId, context: null);

        _completion.Verify(
            service => service.FailAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private async Task<Guid> AddJobAsync(
        int? durableTaskId = null,
        LyricsAlignmentJobStatus status = LyricsAlignmentJobStatus.Queued,
        bool withAudio = true)
    {
        var jobId = Guid.NewGuid();

        await using var context = new AppDbContext(_options);

        context.SongMetadata.Add(new SongMetadata
        {
            Id = 1,
            SongTitle = "Night Drive",
            CreatorId = 1,
            Mp3BlobPath = withAudio ? "abc/abc-music.mp3" : null
        });

        context.LyricsAlignmentJobs.Add(new LyricsAlignmentJob
        {
            JobId = jobId,
            SongMetadataId = 1,
            CreatorId = 1,
            LyricsBlobPath = "abc/abc-lyrics.txt",
            DurableFunctionTaskId = durableTaskId,
            Status = status,
            Step = LyricsAlignmentStep.Submitted,
            StepUpdatedAt = DateTime.UtcNow
        });

        await context.SaveChangesAsync();
        return jobId;
    }

    private sealed class RecordingNotifier : ILyricsAlignmentNotifier
    {
        public List<LyricsAlignmentStep> Steps { get; } = [];

        public Task NotifyAsync(
            int creatorId,
            LyricsAlignmentProgress progress,
            CancellationToken cancellationToken = default)
        {
            Steps.Add(progress.Step);
            return Task.CompletedTask;
        }

        public Task NotifyStepAsync(
            int creatorId,
            Guid jobId,
            LyricsAlignmentStep step,
            string detail = null,
            CancellationToken cancellationToken = default)
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
}
