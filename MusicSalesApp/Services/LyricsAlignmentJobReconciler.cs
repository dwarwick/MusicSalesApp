#nullable enable
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Closes out lyrics-alignment attempts that stopped moving.
/// </summary>
public interface ILyricsAlignmentJobReconciler
{
    // Hangfire resolves filters from Job.Method, which for an interface-registered job is
    // this declaration. The same attribute on the implementation is silently ignored.
    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    Task ReconcileAsync();
}

/// <summary>
/// The second detector under a pipeline whose first detector is application code.
///
/// <para>
/// Worth contrasting with <see cref="SongUploadJobReconciler"/>, because the two look alike and are
/// not. There, an exhausted queue message lands in a poison queue and Azure reports the failure
/// itself; that reconciler is a backstop for the narrow set of cases where no such event will ever
/// arrive, it can only infer death from a stale timestamp, and it is deliberately slow because a
/// wrong guess destroys healthy work.
/// </para>
///
/// <para>
/// A Durable orchestration has no poison queue. Its message is deleted the moment the orchestration
/// is <em>scheduled</em>, so a run that fails hours later produces no platform event at all - the
/// only thing that reports it is the <c>try/except</c> in the orchestrator, which is code we wrote
/// and therefore code that can be wrong or never reached. That makes this a genuine second detector
/// rather than a long-stop.
/// </para>
///
/// <para>
/// And because the orchestration's instance id was recorded when it was started, this one does not
/// have to guess: it <b>asks</b>. That buys a case the audio pipeline has no equivalent of - an
/// orchestration that <em>succeeded</em> but whose terminal callback was lost, where the right answer
/// is to finish the job rather than fail it. Guessing from a timestamp could only ever have got that
/// one wrong.
/// </para>
/// </summary>
public sealed class LyricsAlignmentJobReconciler : ILyricsAlignmentJobReconciler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IDurableTaskClient _durableTaskClient;
    private readonly ILyricsAlignmentCompletionService _completionService;
    private readonly IOptions<MediaProcessingOptions> _options;
    private readonly ILogger<LyricsAlignmentJobReconciler> _logger;

    public LyricsAlignmentJobReconciler(
        IDbContextFactory<AppDbContext> contextFactory,
        IDurableTaskClient durableTaskClient,
        ILyricsAlignmentCompletionService completionService,
        IOptions<MediaProcessingOptions> options,
        ILogger<LyricsAlignmentJobReconciler> logger)
    {
        _contextFactory = contextFactory;
        _durableTaskClient = durableTaskClient;
        _completionService = completionService;
        _options = options;
        _logger = logger;
    }

    public async Task ReconcileAsync()
    {
        var options = _options.Value;
        var now = DateTime.UtcNow;
        var stalledCutoff = now - options.LyricsStalledJobTimeout;
        var unreachableCutoff = now - options.LyricsUnreachableJobTimeout;

        List<StalledAttempt> stalled;
        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            stalled = await context.LyricsAlignmentJobs
                .Where(job => job.Status == LyricsAlignmentJobStatus.Queued
                    || job.Status == LyricsAlignmentJobStatus.Processing)
                .Where(job => job.StepUpdatedAt < stalledCutoff)
                .Select(job => new StalledAttempt(
                    job.JobId, job.DurableFunctionTaskId, job.StepUpdatedAt))
                .ToListAsync();
        }

        if (stalled.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Reviewing {Count} stalled lyrics alignment attempts.", stalled.Count);

        foreach (var attempt in stalled)
        {
            try
            {
                await ReviewAsync(attempt, unreachableCutoff);
            }
            catch (Exception ex)
            {
                // One unresolvable attempt must not stop the sweep reaching the rest.
                _logger.LogWarning(
                    ex, "Could not reconcile lyrics alignment attempt {JobId}.", attempt.JobId);
            }
        }
    }

    private async Task ReviewAsync(StalledAttempt attempt, DateTime unreachableCutoff)
    {
        if (attempt.DurableFunctionTaskId is null)
        {
            // The Hangfire invoker never got as far as starting anything, and has long since
            // exhausted its own retries. Nothing is running, so nothing can report.
            _logger.LogWarning(
                "Lyrics attempt {JobId} never started an orchestration; failing it.", attempt.JobId);

            await _completionService.FailAsync(
                attempt.JobId,
                LyricsAlignmentFailureCodes.StarterUnreachable,
                "Lyric timing could not be started. Please try again.");
            return;
        }

        var status = await _durableTaskClient.GetStatusAsync(attempt.DurableFunctionTaskId.Value);

        if (!status.Answered)
        {
            // "We could not ask" is not "it failed". Left for the next sweep unless it has been
            // unanswerable for so long that no legitimate run could still be going.
            if (attempt.StepUpdatedAt < unreachableCutoff)
            {
                _logger.LogWarning(
                    "Lyrics attempt {JobId} has been unreachable since {StepUpdatedAt}; writing it off.",
                    attempt.JobId,
                    attempt.StepUpdatedAt);

                await _completionService.FailAsync(
                    attempt.JobId,
                    LyricsAlignmentFailureCodes.Abandoned,
                    "Lyric timing stopped unexpectedly. Please try again.");
            }
            else
            {
                _logger.LogInformation(
                    "Could not read the status of lyrics attempt {JobId}; leaving it for the next sweep.",
                    attempt.JobId);
            }

            return;
        }

        switch (status.Status)
        {
            case DurableTaskStatus.Running:
                // Slow, not dead. Nothing to do - and specifically, no liveness refresh here: the
                // attempt's own progress pings own that field, and writing it from the reconciler
                // would hide a run that has genuinely stopped reporting.
                _logger.LogInformation(
                    "Lyrics attempt {JobId} is still running after {Elapsed}.",
                    attempt.JobId,
                    DateTime.UtcNow - attempt.StepUpdatedAt);
                return;

            case DurableTaskStatus.Terminated:
                await _completionService.FailAsync(
                    attempt.JobId,
                    LyricsAlignmentFailureCodes.OrchestrationTerminated,
                    "Lyric timing was cancelled.");
                return;

            case DurableTaskStatus.Failed:
                await _completionService.FailAsync(
                    attempt.JobId,
                    LyricsAlignmentFailureCodes.OrchestrationFailed,
                    "Lyric timing failed. Please try again.");
                return;

            case DurableTaskStatus.Completed:
                await RecoverLostCallbackAsync(attempt, status.Output);
                return;
        }
    }

    /// <summary>
    /// Finishes an attempt whose orchestration succeeded but whose terminal callback never arrived.
    ///
    /// <para>
    /// The case that justifies recording the instance id at all. Without it this attempt looks
    /// exactly like a dead one, and the only available answer is to fail a song whose timings were
    /// computed successfully and are sitting in staging - minutes of compute thrown away for a lost
    /// HTTP request.
    /// </para>
    /// </summary>
    private async Task RecoverLostCallbackAsync(StalledAttempt attempt, string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.LogWarning(
                "Lyrics attempt {JobId} completed but reported no output; failing it.", attempt.JobId);

            await _completionService.FailAsync(
                attempt.JobId,
                LyricsAlignmentFailureCodes.AlignmentFailed,
                "Lyric timing finished but produced no result. Please try again.");
            return;
        }

        LyricsAlignmentResult? result;
        try
        {
            result = JsonSerializer.Deserialize<LyricsAlignmentResult>(output, SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "Lyrics attempt {JobId} completed with unreadable output; failing it.", attempt.JobId);

            await _completionService.FailAsync(
                attempt.JobId,
                LyricsAlignmentFailureCodes.AlignmentFailed,
                "Lyric timing finished but its result could not be read. Please try again.");
            return;
        }

        if (result is null)
        {
            await _completionService.FailAsync(
                attempt.JobId,
                LyricsAlignmentFailureCodes.AlignmentFailed,
                "Lyric timing finished but produced no result. Please try again.");
            return;
        }

        // The orchestration's own JobId is authoritative for correlation, but a mismatched or empty
        // one in the recovered output would silently complete the wrong attempt.
        result.JobId = attempt.JobId;

        _logger.LogInformation(
            "Recovering lyrics attempt {JobId} whose terminal callback was lost.", attempt.JobId);

        await _completionService.CompleteAsync(result);
    }

    private sealed record StalledAttempt(Guid JobId, int? DurableFunctionTaskId, DateTime StepUpdatedAt);
}
