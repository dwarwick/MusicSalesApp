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
/// The reconciler, which for lyrics is a genuine second detector rather than a long-stop.
///
/// <para>
/// Worth contrasting with <c>SongUploadJobReconciler</c>, because they look alike and are not.
/// There, Azure's poison queue reports a dead upload authoritatively, and the reconciler is a
/// backstop that can only infer death from a stale timestamp. A Durable orchestration has no poison
/// queue - its trigger message is deleted the moment the run is scheduled - so the only prompt
/// detector is a <c>try/except</c> in the orchestrator, which is code that can be wrong or never
/// reached.
/// </para>
///
/// <para>
/// Because the instance id was recorded when the run started, this one does not guess: it asks. The
/// tests below are mostly about the four different answers it can get back, and about the one that
/// only becomes reachable <em>because</em> it asks - an orchestration that succeeded but whose
/// callback was lost, where the right answer is to finish the job rather than fail it.
/// </para>
/// </summary>
[TestFixture]
public class LyricsAlignmentJobReconcilerTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IDurableTaskClient> _durable = null!;
    private Mock<ILyricsAlignmentCompletionService> _completion = null!;
    private LyricsAlignmentJobReconciler _reconciler = null!;

    private static MediaProcessingOptions Settings => new();

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"lyrics-reconciler-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);
        _durable = new Mock<IDurableTaskClient>();
        _completion = new Mock<ILyricsAlignmentCompletionService>();

        _reconciler = new LyricsAlignmentJobReconciler(
            _factory,
            _durable.Object,
            _completion.Object,
            Options.Create(Settings),
            Mock.Of<ILogger<LyricsAlignmentJobReconciler>>());
    }

    [Test]
    public async Task AnAttemptThatIsStillMovingIsLeftAlone()
    {
        await AddJobAsync(stepAge: TimeSpan.FromMinutes(5), durableTaskId: 1);

        await _reconciler.ReconcileAsync();

        _durable.Verify(
            client => client.GetStatusAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "A job inside the timeout should not even be asked about.");
    }

    [Test]
    public async Task AStalledButStillRunningAttemptIsNotFailed()
    {
        // The case the audio reconciler cannot distinguish at all. Separation genuinely runs for
        // tens of minutes; killing it here would throw away work that was about to finish.
        await AddStalledJobAsync(durableTaskId: 1);
        GivenStatus(DurableTaskStatus.Running);

        await _reconciler.ReconcileAsync();

        _completion.Verify(
            service => service.FailAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task AFailedOrchestrationIsReportedWithTheRealReason()
    {
        await AddStalledJobAsync(durableTaskId: 1);
        GivenStatus(DurableTaskStatus.Failed);

        await _reconciler.ReconcileAsync();

        _completion.Verify(
            service => service.FailAsync(
                It.IsAny<Guid>(),
                LyricsAlignmentFailureCodes.OrchestrationFailed,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ATerminatedOrchestrationIsNotReportedAsAFault()
    {
        // Somebody cancelled it. Telling the creator their song broke would be wrong, and it is the
        // sort of wrong that generates a support ticket.
        await AddStalledJobAsync(durableTaskId: 1);
        GivenStatus(DurableTaskStatus.Terminated);

        await _reconciler.ReconcileAsync();

        _completion.Verify(
            service => service.FailAsync(
                It.IsAny<Guid>(),
                LyricsAlignmentFailureCodes.OrchestrationTerminated,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ASuccessfulOrchestrationWhoseCallbackWasLostIsFinishedRatherThanFailed()
    {
        // THE case that justifies recording the instance id. Without it this looks exactly like a
        // dead attempt, and the only available answer is to fail a song whose timings were computed
        // successfully and are sitting in staging - tens of minutes of billed CPU thrown away
        // because one HTTP request went missing.
        var jobId = await AddStalledJobAsync(durableTaskId: 1);

        var output = System.Text.Json.JsonSerializer.Serialize(new
        {
            jobId = Guid.Empty,
            outcome = "Aligned",
            confidence = 0.9,
            isMonotonic = true,
            matchedTokenCount = 100
        });

        _durable
            .Setup(client => client.GetStatusAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableStatusOutcome(true, DurableTaskStatus.Completed, "Completed", output, null));

        await _reconciler.ReconcileAsync();

        _completion.Verify(
            service => service.CompleteAsync(
                It.Is<LyricsAlignmentResult>(result => result.JobId == jobId),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "The recovered result must be re-keyed to the attempt being reconciled.");

        _completion.Verify(
            service => service.FailAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task AnAttemptThatNeverStartedAnOrchestrationIsFailed()
    {
        // The Hangfire invoker exhausted its retries without ever reaching the Function app, so
        // nothing is running and nothing will ever report.
        await AddStalledJobAsync(durableTaskId: null);

        await _reconciler.ReconcileAsync();

        _completion.Verify(
            service => service.FailAsync(
                It.IsAny<Guid>(),
                LyricsAlignmentFailureCodes.StarterUnreachable,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task AnUnreachableOrchestrationIsLeftForTheNextSweepRatherThanFailed()
    {
        // "We could not ask" is not a verdict. Treating it as one would fail every healthy run for
        // the duration of an outage - which is precisely the bug the audio pipeline's history is a
        // record of.
        await AddStalledJobAsync(durableTaskId: 1);
        GivenUnanswerable();

        await _reconciler.ReconcileAsync();

        _completion.Verify(
            service => service.FailAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task AnAttemptUnreachableForFarTooLongIsEventuallyWrittenOff()
    {
        // It cannot be left forever either, or the creator watches a frozen bar indefinitely. The
        // point is that it takes many failed sweeps first, not one.
        await AddJobAsync(
            stepAge: Settings.LyricsUnreachableJobTimeout + TimeSpan.FromHours(1), durableTaskId: 1);
        GivenUnanswerable();

        await _reconciler.ReconcileAsync();

        _completion.Verify(
            service => service.FailAsync(
                It.IsAny<Guid>(),
                LyricsAlignmentFailureCodes.Abandoned,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task OneUnresolvableAttemptDoesNotStopTheSweepReachingTheRest()
    {
        await AddStalledJobAsync(durableTaskId: 1);
        var second = await AddStalledJobAsync(durableTaskId: 2, songMetadataId: 2);

        _durable
            .Setup(client => client.GetStatusAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _durable
            .Setup(client => client.GetStatusAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableStatusOutcome(true, DurableTaskStatus.Failed, "Failed", null, null));

        await _reconciler.ReconcileAsync();

        _completion.Verify(
            service => service.FailAsync(
                second, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void GivenStatus(DurableTaskStatus status)
        => _durable
            .Setup(client => client.GetStatusAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableStatusOutcome(true, status, status.ToString(), null, null));

    private void GivenUnanswerable()
        => _durable
            .Setup(client => client.GetStatusAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableStatusOutcome(false, DurableTaskStatus.Running, null, null, "unreachable"));

    private Task<Guid> AddStalledJobAsync(int? durableTaskId, int songMetadataId = 1)
        => AddJobAsync(
            Settings.LyricsStalledJobTimeout + TimeSpan.FromMinutes(10), durableTaskId, songMetadataId);

    private async Task<Guid> AddJobAsync(TimeSpan stepAge, int? durableTaskId, int songMetadataId = 1)
    {
        var jobId = Guid.NewGuid();

        await using var context = new AppDbContext(_options);
        context.LyricsAlignmentJobs.Add(new LyricsAlignmentJob
        {
            JobId = jobId,
            SongMetadataId = songMetadataId,
            CreatorId = 1,
            LyricsBlobPath = "abc/abc-lyrics.txt",
            DurableFunctionTaskId = durableTaskId,
            Status = LyricsAlignmentJobStatus.Processing,
            Step = LyricsAlignmentStep.SeparatingVocals,
            StepUpdatedAt = DateTime.UtcNow - stepAge
        });
        await context.SaveChangesAsync();

        return jobId;
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
