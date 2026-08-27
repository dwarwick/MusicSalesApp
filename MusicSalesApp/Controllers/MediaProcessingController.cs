#nullable enable
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Middleware;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// The callback surface the audio-processing Azure Function posts to.
///
/// <para>
/// The Function has no database access - it runs in Azure while the site and its SQL Server run on
/// shared hosting - so everything it learns comes back through here. Authorised by a dedicated
/// shared secret rather than a user identity, since there is no user on the other end.
/// </para>
/// </summary>
[ApiController]
[Route(MediaProcessingRoutes.ControllerRoute)]
[RequireMediaProcessingApiKey]
public class MediaProcessingController : ControllerBase
{
    private readonly IMediaProcessingCompletionService _completionService;
    private readonly IAudioProbeResultHandler _probeResultHandler;
    private readonly IHlsPackagingResultHandler _packagingResultHandler;
    private readonly IUploadProgressNotifier _progressNotifier;
    private readonly ICoverArtMatchNotifier _matchNotifier;
    private readonly ILyricsAlignmentCompletionService _lyricsCompletionService;
    private readonly ILyricsAlignmentNotifier _lyricsNotifier;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<MediaProcessingController> _logger;

    /// <summary>
    /// How stale a job's liveness timestamp may get before a non-advancing progress ping is allowed
    /// to refresh it. Far below the reconciler's timeout, far above the rate progress arrives at.
    /// </summary>
    private static readonly TimeSpan LivenessRefreshInterval = TimeSpan.FromMinutes(1);

    public MediaProcessingController(
        IMediaProcessingCompletionService completionService,
        IAudioProbeResultHandler probeResultHandler,
        IHlsPackagingResultHandler packagingResultHandler,
        IUploadProgressNotifier progressNotifier,
        ICoverArtMatchNotifier matchNotifier,
        ILyricsAlignmentCompletionService lyricsCompletionService,
        ILyricsAlignmentNotifier lyricsNotifier,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<MediaProcessingController> logger)
    {
        _completionService = completionService;
        _probeResultHandler = probeResultHandler;
        _packagingResultHandler = packagingResultHandler;
        _progressNotifier = progressNotifier;
        _matchNotifier = matchNotifier;
        _lyricsCompletionService = lyricsCompletionService;
        _lyricsNotifier = lyricsNotifier;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// The terminal callback for one upload. Assembles the song, or records the failure.
    ///
    /// <para>
    /// Returning a non-2xx here makes the Function throw, which returns the message to the queue
    /// for another attempt - so only genuinely retryable problems should produce one.
    /// </para>
    /// </summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] AudioTranscodeResult result, CancellationToken cancellationToken)
    {
        if (result is null || result.JobId == Guid.Empty)
        {
            return BadRequest(new { message = "A job id is required." });
        }

        await _completionService.CompleteAsync(result, cancellationToken);
        return Ok();
    }


    /// <summary>
    /// The result of one encrypted-HLS packaging run.
    ///
    /// <para>
    /// <b>This is the only callback whose body carries a secret</b> — the song's content key, in the
    /// clear. It is safe because the whole route is gated by <c>X-Media-Processing-Key</c> and runs
    /// over HTTPS, but it means the body must never be logged, echoed in an error, or included in a
    /// diagnostic dump.
    /// </para>
    /// </summary>
    [HttpPost("package-result")]
    public async Task<IActionResult> PackageResult(
        [FromBody] AudioPackageResult result,
        CancellationToken cancellationToken)
    {
        if (result is null || result.SongMetadataId <= 0)
        {
            return BadRequest(new { message = "A song id is required." });
        }

        await _packagingResultHandler.HandleAsync(result, cancellationToken);
        return Ok();
    }

    /// <summary>The result of one decode-only probe, for the audit or the track-length repair job.</summary>
    [HttpPost("probe-result")]
    public async Task<IActionResult> ProbeResult([FromBody] AudioProbeResult result, CancellationToken cancellationToken)
    {
        if (result is null || result.SongMetadataId <= 0)
        {
            return BadRequest(new { message = "A song id is required." });
        }

        await _probeResultHandler.HandleAsync(result, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// A progress ping. Best effort in both directions: the Function swallows failures posting
    /// these, and this action never treats a stale or unknown update as an error.
    /// </summary>
    [HttpPost("progress")]
    public async Task<IActionResult> Progress([FromBody] AudioProcessingProgress progress, CancellationToken cancellationToken)
    {
        if (progress is null || progress.JobId == Guid.Empty)
        {
            return BadRequest(new { message = "A job id is required." });
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.SongUploadJobs.FirstOrDefaultAsync(
            j => j.MediaGuid == progress.JobId,
            cancellationToken);

        if (job is null)
        {
            // A ping for a job that has been cleaned up. Normal, not an error - and answering
            // anything but 200 would make the Function retry a cosmetic update.
            return Ok();
        }

        if (AudioProcessingProgressCalculator.IsTerminal(job.Step))
        {
            // A late in-flight ping arriving after the song already finished or failed. Dropping it
            // is what stops a completed bar from jumping back to 60%.
            return Ok();
        }

        var isAdvance = AudioProcessingProgressCalculator.IsAdvance(job.Step, progress.Step);

        // Any ping at all proves the Function is still working on this job, whatever step it names.
        // That matters because a queue retry restarts at Downloading and replays steps the job has
        // already passed - none of which are advances. Without refreshing liveness here,
        // StepUpdatedAt would freeze at the moment of the first attempt's furthest step, and
        // SongUploadJobReconciler would fail a job mid-retry and delete the staging its next
        // attempt needs.
        //
        // Throttled so this stays a step-transition-frequency write rather than a
        // percentage-tick-frequency one: hundreds of writes per song against shared-hosting SQL is
        // what the original design was avoiding.
        var liveness = DateTime.UtcNow;
        var livenessIsStale = liveness - job.StepUpdatedAt > LivenessRefreshInterval;

        if (isAdvance)
        {
            job.Step = progress.Step;
            job.Status = SongUploadJobStatus.Processing;
            job.StepUpdatedAt = liveness;
            await context.SaveChangesAsync(cancellationToken);
        }
        else if (livenessIsStale)
        {
            job.StepUpdatedAt = liveness;
            await context.SaveChangesAsync(cancellationToken);
        }

        if (!isAdvance && progress.Step < job.Step)
        {
            // Out of order: a retry replaying an earlier step, or two posts racing. The liveness
            // refresh above still counted it, but the creator's bar must not run backwards.
            _logger.LogDebug(
                "Ignoring stale progress for job {JobId}: {Incoming} is behind {Current}",
                progress.JobId,
                progress.Step,
                job.Step);
            return Ok();
        }

        await _progressNotifier.NotifyAsync(job.CreatorId, progress, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// The terminal callback for one lyrics-alignment attempt. Stores the timings, or records why
    /// there are none.
    ///
    /// <para>
    /// Same contract as <see cref="Complete"/>: a non-2xx makes the Function throw and retry, so only
    /// genuinely retryable problems should produce one. Note the caller here is a different Function
    /// app in a different language, sharing this controller and this secret because it is the same
    /// kind of caller - a trusted background worker with no user behind it.
    /// </para>
    /// </summary>
    [HttpPost("lyrics-complete")]
    public async Task<IActionResult> LyricsComplete(
        [FromBody] LyricsAlignmentResult result,
        CancellationToken cancellationToken)
    {
        if (result is null || result.JobId == Guid.Empty)
        {
            return BadRequest(new { message = "A job id is required." });
        }

        await _lyricsCompletionService.CompleteAsync(result, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// A lyrics progress ping. Best effort in both directions, exactly like <see cref="Progress"/>.
    /// </summary>
    [HttpPost("lyrics-progress")]
    public async Task<IActionResult> LyricsProgress(
        [FromBody] LyricsAlignmentProgress progress,
        CancellationToken cancellationToken)
    {
        if (progress is null || progress.JobId == Guid.Empty)
        {
            return BadRequest(new { message = "A job id is required." });
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.LyricsAlignmentJobs.FirstOrDefaultAsync(
            row => row.JobId == progress.JobId,
            cancellationToken);

        if (job is null)
        {
            // A ping for an attempt that has been cleaned up. Normal, not an error - and answering
            // anything but 200 would make the Function retry a cosmetic update.
            return Ok();
        }

        if (LyricsAlignmentProgressCalculator.IsTerminal(job.Step))
        {
            // A late ping arriving after the attempt already finished, failed or was cancelled.
            // Dropping it is what stops a finished bar from jumping backwards.
            return Ok();
        }

        var isAdvance = LyricsAlignmentProgressCalculator.IsAdvance(job.Step, progress.Step);

        // Any ping at all proves the orchestration is still working, whatever step it names - and
        // that matters more here than in the audio pipeline. Vocal separation runs for minutes with
        // no natural progress signal, so the activity heartbeats on a timer rather than advancing;
        // without refreshing liveness on a non-advancing ping, every one of those would leave
        // StepUpdatedAt frozen and the reconciler would start asking about a perfectly healthy run.
        //
        // Throttled so this stays a step-transition-frequency write rather than a
        // heartbeat-frequency one.
        var liveness = DateTime.UtcNow;
        var livenessIsStale = liveness - job.StepUpdatedAt > LivenessRefreshInterval;

        if (isAdvance)
        {
            job.Step = progress.Step;
            job.Status = LyricsAlignmentJobStatus.Processing;
            job.StepUpdatedAt = liveness;
            await context.SaveChangesAsync(cancellationToken);
        }
        else if (livenessIsStale)
        {
            job.StepUpdatedAt = liveness;
            await context.SaveChangesAsync(cancellationToken);
        }

        if (!isAdvance && progress.Step < job.Step)
        {
            _logger.LogDebug(
                "Ignoring stale lyrics progress for job {JobId}: {Incoming} is behind {Current}",
                progress.JobId,
                progress.Step,
                job.Step);
            return Ok();
        }

        await _lyricsNotifier.NotifyAsync(job.CreatorId, progress, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// The terminal callback for one cover-art matching batch. Relays the pairing to the creator's
    /// upload page, which is blocked waiting for it.
    ///
    /// <para>
    /// Nothing is persisted, because a match batch has no row: it exists only while the page is
    /// waiting, its staged images are swept by the container's lifecycle rule, and a lost message
    /// costs a worse pairing rather than a lost song. The consequence is that this has no free
    /// idempotency — a redelivery broadcasts twice — which is why the page resolves its waiting task
    /// with <c>TrySetResult</c> and treats the second one as a no-op.
    /// </para>
    ///
    /// <para>
    /// <b>The creator id here came from the Function, which got it from the queue message this site
    /// wrote.</b> It is used for the SignalR group name and nothing else, and this endpoint sits
    /// behind the shared processing secret — so the worst a compromised key could do here is push a
    /// bogus pairing into one creator's page, strictly less than <c>complete</c> already permits.
    /// It is still the only place in this codebase where a caller-supplied creator id reaches a
    /// group name, so do not widen what it is used for.
    /// </para>
    /// </summary>
    [HttpPost("match-complete")]
    public async Task<IActionResult> MatchComplete(
        [FromBody] CoverArtMatchResult result,
        CancellationToken cancellationToken)
    {
        if (result is null || result.BatchId == Guid.Empty)
        {
            return BadRequest(new { message = "A batch id is required." });
        }

        await _matchNotifier.NotifyResultAsync(result, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// A progress ping for a cover-art matching batch. Relayed straight through: with no row there
    /// is nothing to persist, nothing to compare a step against, and nothing a bad value could
    /// corrupt. Always 200, for the same reason the audio version is — a non-2xx would make the
    /// Function retry a cosmetic update.
    /// </summary>
    [HttpPost("match-progress")]
    public async Task<IActionResult> MatchProgress(
        [FromBody] CoverArtMatchProgress progress,
        CancellationToken cancellationToken)
    {
        if (progress is null || progress.BatchId == Guid.Empty)
        {
            return Ok();
        }

        await _matchNotifier.NotifyProgressAsync(progress, cancellationToken);
        return Ok();
    }
}
