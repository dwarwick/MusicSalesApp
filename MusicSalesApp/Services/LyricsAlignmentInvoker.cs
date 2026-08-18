#nullable enable
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Starts the alignment orchestration for one submitted attempt.
/// </summary>
public interface ILyricsAlignmentInvoker
{
    Task InvokeAsync(Guid jobId, PerformContext? context);
}

/// <summary>
/// The bridge between "the creator pressed Submit" and "an orchestration is running in Azure".
///
/// <para>
/// A Hangfire job rather than an inline call, for two reasons. It gets a multi-second HTTP round trip
/// off the Blazor circuit, and more importantly it supplies the durable retry that the audio pipeline
/// gets from its storage queue. Lyrics alignment has no queue - it is invoked over HTTP precisely so
/// the orchestration's instance id comes back - so something else has to survive the Function app
/// being briefly unreachable, and Hangfire was already in the stack doing exactly that for other work.
/// </para>
///
/// <para>
/// <b>The re-entrancy guard is the important part of this class.</b> Hangfire retries on any
/// exception, and a start that succeeded but whose response was lost would otherwise be retried into
/// a second orchestration - two Demucs runs billed for one attempt, both calling back, with the
/// second arriving after the first had already published. The job row's
/// <see cref="LyricsAlignmentJob.DurableFunctionTaskId"/> is the interlock: non-null means this has
/// already run to completion, whatever the retry thinks.
/// </para>
/// </summary>
public sealed class LyricsAlignmentInvoker : ILyricsAlignmentInvoker
{
    /// <summary>
    /// The orchestrator's name, matching the Python <c>@app.orchestration_trigger</c>. Recorded on
    /// the task row, which is what keeps one durable function's rows apart from another's.
    /// </summary>
    public const string OrchestratorName = "align_lyrics_orchestrator";

    /// <summary>
    /// Hangfire's own retry budget for this job. Three attempts with Hangfire's default backoff
    /// covers a Function app restarting or a brief network fault, without spending long on a Function
    /// app that is simply not there - the creator is watching a progress bar, and a wrong answer
    /// quickly beats a right one an hour later.
    /// </summary>
    public const int MaxAttempts = 3;

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IDurableTaskClient _durableTaskClient;
    private readonly ILyricsAlignmentNotifier _notifier;
    private readonly ILyricsAlignmentCompletionService _completionService;
    private readonly ILogger<LyricsAlignmentInvoker> _logger;

    public LyricsAlignmentInvoker(
        IDbContextFactory<AppDbContext> contextFactory,
        IDurableTaskClient durableTaskClient,
        ILyricsAlignmentNotifier notifier,
        ILyricsAlignmentCompletionService completionService,
        ILogger<LyricsAlignmentInvoker> logger)
    {
        _contextFactory = contextFactory;
        _durableTaskClient = durableTaskClient;
        _notifier = notifier;
        _completionService = completionService;
        _logger = logger;
    }

    /// <inheritdoc />
    [AutomaticRetry(Attempts = MaxAttempts)]
    public async Task InvokeAsync(Guid jobId, PerformContext? context)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var job = await dbContext.LyricsAlignmentJobs
            .Include(row => row.SongMetadata)
            .FirstOrDefaultAsync(row => row.JobId == jobId);

        if (job is null)
        {
            // Nothing to do and nothing wrong: the song may have been deleted, which cascades the
            // attempt away. Throwing would retry a job that can never succeed.
            _logger.LogInformation("Lyrics alignment job {JobId} no longer exists; nothing to start.", jobId);
            return;
        }

        if (job.DurableFunctionTaskId is not null)
        {
            // The re-entrancy guard. See the class remarks - this is what stops a retry from billing
            // a second separation run.
            _logger.LogInformation(
                "Lyrics alignment job {JobId} already has orchestration {TaskId}; not starting another.",
                jobId,
                job.DurableFunctionTaskId);
            return;
        }

        if (job.Status is LyricsAlignmentJobStatus.Completed or LyricsAlignmentJobStatus.Failed)
        {
            _logger.LogInformation(
                "Lyrics alignment job {JobId} is already {Status}; not starting an orchestration.",
                jobId,
                job.Status);
            return;
        }

        if (job.SongMetadata is null || string.IsNullOrWhiteSpace(job.SongMetadata.Mp3BlobPath))
        {
            _logger.LogWarning(
                "Lyrics alignment job {JobId} failed before starting: no playable audio.", jobId);

            await _completionService.FailAsync(
                job.JobId,
                LyricsAlignmentFailureCodes.AudioBlobMissing,
                "This song has no playable audio to align against.");
            return;
        }

        var request = new LyricsAlignmentRequest
        {
            JobId = job.JobId,
            SongMetadataId = job.SongMetadataId,
            CreatorId = job.CreatorId,
            Mp3BlobPath = job.SongMetadata.Mp3BlobPath,
            LyricsBlobPath = job.LyricsBlobPath,
            RequestedUtc = job.CreatedAt
        };

        var outcome = await _durableTaskClient.StartAsync(
            OrchestratorName,
            LyricsStarterRoutes.Start,
            request);

        if (outcome.Started && outcome.Task is not null)
        {
            job.DurableFunctionTaskId = outcome.Task.Id;
            job.Status = LyricsAlignmentJobStatus.Processing;
            job.Step = LyricsAlignmentStep.Queued;
            job.StepUpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();

            await _notifier.NotifyStepAsync(
                job.CreatorId,
                job.JobId,
                LyricsAlignmentStep.Queued,
                "Waiting for a worker to pick this up.");

            return;
        }

        // Could not start. Retry while attempts remain; on the last one, say so rather than leaving
        // the creator's bar frozen until the reconciler notices ninety minutes later.
        if (IsFinalAttempt(context))
        {
            _logger.LogWarning(
                "Lyrics alignment job {JobId} failed before starting: the starter was unreachable "
                + "on the final attempt.", jobId);

            await _completionService.FailAsync(
                job.JobId,
                LyricsAlignmentFailureCodes.StarterUnreachable,
                "Lyric timing is temporarily unavailable. Please try again shortly.");
            return;
        }

        throw new InvalidOperationException(
            $"Could not start the lyrics orchestration for job {jobId}: {outcome.FailureDetail}");
    }

    /// <summary>
    /// Whether Hangfire has no retries left for this execution.
    ///
    /// <para>
    /// Hangfire counts retries in a job parameter, absent on the first run. Treating an absent or
    /// unreadable context as "not the last attempt" is the safe default: the cost of being wrong is
    /// one more retry, whereas the opposite mistake reports a permanent failure while attempts remain.
    /// </para>
    /// </summary>
    private static bool IsFinalAttempt(PerformContext? context)
    {
        if (context is null)
        {
            return false;
        }

        try
        {
            return context.GetJobParameter<int>("RetryCount") >= MaxAttempts;
        }
        catch
        {
            return false;
        }
    }
}
