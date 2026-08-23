#nullable enable
using Azure.Storage.Blobs.Models;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// What the web app decided a set of timings is worth.
/// </summary>
public sealed record LyricsClassification(SongLyricsStatus Status, string? FailureCode, string? Message);

/// <summary>
/// Turns a finished alignment into stored timings.
/// </summary>
public interface ILyricsAlignmentCompletionService
{
    /// <summary>
    /// Copies the timing artifacts into the media container and updates the song's lyrics row.
    ///
    /// <para>
    /// Idempotent. The Function retries its terminal callback on any non-2xx, and the reconciler can
    /// drive this too when a callback was lost, so a second call for an already-finished attempt must
    /// be a no-op rather than a second set of timings.
    /// </para>
    /// </summary>
    Task CompleteAsync(LyricsAlignmentResult result, CancellationToken cancellationToken = default);

    /// <summary>Records that an attempt failed and tells the creator why.</summary>
    Task FailAsync(
        Guid jobId,
        string failureCode,
        string message,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class LyricsAlignmentCompletionService : ILyricsAlignmentCompletionService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IBlobContainerFactory _containerFactory;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IStagingToMediaCopier _copier;
    private readonly ILyricsAlignmentNotifier _notifier;
    private readonly ILogger<LyricsAlignmentCompletionService> _logger;

    // No IAppSettingsService here any more: the only thing it was asked for was the lyrics confidence
    // threshold, and classification no longer consults the score at all.
    public LyricsAlignmentCompletionService(
        IDbContextFactory<AppDbContext> contextFactory,
        IBlobContainerFactory containerFactory,
        IBackgroundJobClient backgroundJobs,
        IStagingToMediaCopier copier,
        ILyricsAlignmentNotifier notifier,
        ILogger<LyricsAlignmentCompletionService> logger)
    {
        _contextFactory = contextFactory;
        _containerFactory = containerFactory;
        _backgroundJobs = backgroundJobs;
        _copier = copier;
        _notifier = notifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        LyricsAlignmentResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var job = await context.LyricsAlignmentJobs
            .Include(row => row.SongMetadata)
            .FirstOrDefaultAsync(row => row.JobId == result.JobId, cancellationToken);

        if (job is null)
        {
            // Not an error, and emphatically not a non-2xx: the song may have been deleted, which
            // cascades the attempt away. Answering with a failure would make the Function retry a
            // callback that can never be accepted, forever.
            _logger.LogInformation(
                "Lyrics completion callback for unknown job {JobId}; ignoring.", result.JobId);
            return;
        }

        if (job.Status is LyricsAlignmentJobStatus.Completed or LyricsAlignmentJobStatus.Failed)
        {
            // The retry path, not an error. Failed must be caught here as well as Completed: an
            // attempt the creator cancelled, or one the reconciler already closed, will still get its
            // callback when the orchestration eventually finishes, and that late arrival must not
            // resurrect it.
            _logger.LogInformation(
                "Lyrics completion callback replayed for already-{Status} job {JobId}.",
                job.Status,
                result.JobId);

            await DeleteStagedOutputAsync(result.JobId, cancellationToken);
            return;
        }

        if (result.Outcome != LyricsAlignmentOutcome.Aligned)
        {
            // Inconclusive lands here alongside Unusable, and the reason is worth stating. In the
            // audio pipeline "the tool could not run" is answered by throwing, so the queue retries
            // on another instance. There is no queue here, and the orchestration has already
            // exhausted its own activity retries by the time it reports - so the honest answer is to
            // record it and offer the creator a Re-run, rather than pretend a retry is still coming.
            var code = result.FailureCode
                ?? (result.Outcome == LyricsAlignmentOutcome.Inconclusive
                    ? LyricsAlignmentFailureCodes.AlignmentFailed
                    : LyricsAlignmentFailureCodes.NoTokensMatched);

            await ApplyFailureAsync(context, job, code, DescribeFailure(code), cancellationToken);
            await DeleteStagedOutputAsync(result.JobId, cancellationToken);
            return;
        }

        var classification = Classify(result);

        if (classification.Status == SongLyricsStatus.Failed)
        {
            await ApplyFailureAsync(
                context,
                job,
                classification.FailureCode ?? LyricsAlignmentFailureCodes.AlignmentFailed,
                classification.Message ?? "The timings produced were not usable.",
                cancellationToken);

            await DeleteStagedOutputAsync(result.JobId, cancellationToken);
            return;
        }

        var song = job.SongMetadata;
        if (song is null)
        {
            _logger.LogWarning("Lyrics job {JobId} has no song; ignoring its result.", result.JobId);
            return;
        }

        var staging = _containerFactory.GetUploadStagingContainer();
        var media = _containerFactory.GetMediaContainer();

        if (staging is null)
        {
            throw new InvalidOperationException(
                "Cannot assemble lyrics timings: the upload staging container is not configured.");
        }

        var timingsTarget = SongMediaPaths.ResolveLyricsTimingsTarget(
            song.Id, song.MediaGuid, song.Mp3BlobPath);
        var lrcTarget = SongMediaPaths.ResolveLyricsLrcTarget(
            song.Id, song.MediaGuid, song.Mp3BlobPath);

        await AdvanceAsync(context, job, LyricsAlignmentStep.Copying, cancellationToken);

        using var assemblyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        assemblyTimeout.CancelAfter(LyricsProcessingTimeouts.Assembly);
        var assemblyToken = assemblyTimeout.Token;

        var sasQuery = _copier.CreateStagingReadSasQuery(staging);

        await _copier.CopyAsync(
            staging,
            result.TimingsBlobPath ?? MediaProcessingStagingPaths.LyricsTimings(result.JobId),
            media,
            timingsTarget,
            sasQuery,
            LyricsProcessingTimeouts.Assembly,
            assemblyToken,
            cancellationToken);

        // The LRC is an export convenience rather than the primary artifact, so a failure to copy it
        // must not cost the creator their timings. The JSON is already in place by this point.
        var lrcCopied = true;
        try
        {
            await _copier.CopyAsync(
                staging,
                result.LrcBlobPath ?? MediaProcessingStagingPaths.LyricsLrc(result.JobId),
                media,
                lrcTarget,
                sasQuery,
                LyricsProcessingTimeouts.Assembly,
                assemblyToken,
                cancellationToken);
        }
        catch (Exception ex)
        {
            lrcCopied = false;
            _logger.LogWarning(
                ex, "Could not copy the LRC for job {JobId}; publishing the timings anyway.", result.JobId);
        }

        await AdvanceAsync(context, job, LyricsAlignmentStep.Saving, cancellationToken);

        var now = DateTime.UtcNow;

        var lyrics = await context.SongLyrics
            .FirstOrDefaultAsync(row => row.SongMetadataId == job.SongMetadataId, cancellationToken);

        if (lyrics is null)
        {
            // Only reachable if the row was deleted between submission and completion, but writing
            // one is strictly better than dropping timings that cost minutes of compute.
            lyrics = new SongLyrics
            {
                SongMetadataId = job.SongMetadataId,
                LyricsBlobPath = job.LyricsBlobPath,
                CreatedAt = now
            };
            context.SongLyrics.Add(lyrics);
        }

        lyrics.TimingsBlobPath = timingsTarget;
        lyrics.LrcBlobPath = lrcCopied ? lrcTarget : null;
        lyrics.Status = classification.Status;
        lyrics.Confidence = result.Confidence;
        lyrics.AlignedAt = now;
        lyrics.LastJobId = job.JobId;
        lyrics.UpdatedAt = now;

        // THE DRAFT IS DISCARDED, because it describes timings that no longer exist. A draft is a set
        // of edits to a specific alignment - line four starts here, this word is 50 ms late - and a
        // re-alignment has just replaced every one of those timings with new ones. Keeping it would
        // silently reapply corrections to a document they were never made against, which is worse
        // than losing them: the creator would open the editor to a song that had somehow got worse.
        var staleDraft = lyrics.DraftTimingsBlobPath;
        lyrics.DraftTimingsBlobPath = null;
        lyrics.DraftUpdatedAt = null;

        // The paths never change, so nothing downstream would notice a re-alignment without this.
        lyrics.Version++;

        job.Status = LyricsAlignmentJobStatus.Completed;
        job.Step = LyricsAlignmentStep.Completed;
        job.StepUpdatedAt = now;
        job.CompletedAt = now;
        job.Confidence = result.Confidence;

        if (job.DurableFunctionTaskId is not null)
        {
            var task = await context.DurableFunctionTasks
                .FirstOrDefaultAsync(row => row.Id == job.DurableFunctionTaskId.Value, cancellationToken);

            if (task is not null && task.Status == DurableTaskStatus.Running)
            {
                task.Status = DurableTaskStatus.Completed;
                task.RuntimeStatusRaw = "Completed";
                task.CompletedAt = now;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyStepAsync(
            job.CreatorId,
            job.JobId,
            LyricsAlignmentStep.Completed,
            classification.Message);

        QueueCompletionEmail(job.JobId);

        await DeleteStagedOutputAsync(result.JobId, cancellationToken);

        // After the save, and best-effort. A draft blob left behind is unreachable - nothing points
        // at it any more - so failing the callback over it would trade a working alignment for a
        // tidy container.
        if (!string.IsNullOrWhiteSpace(staleDraft))
        {
            try
            {
                await _containerFactory.GetMediaContainer()
                    .GetBlobClient(staleDraft)
                    .DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete the superseded draft timings {Path}.", staleDraft);
            }
        }

        _logger.LogInformation(
            "Stored lyrics timings for song {SongId} as {Status} (confidence {Confidence:0.00}).",
            job.SongMetadataId,
            classification.Status,
            result.Confidence ?? 0d);
    }

    /// <inheritdoc />
    public async Task FailAsync(
        Guid jobId,
        string failureCode,
        string message,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var job = await context.LyricsAlignmentJobs
            .FirstOrDefaultAsync(row => row.JobId == jobId, cancellationToken);

        if (job is null)
        {
            return;
        }

        if (job.Status is LyricsAlignmentJobStatus.Completed or LyricsAlignmentJobStatus.Failed)
        {
            return;
        }

        await ApplyFailureAsync(context, job, failureCode, message, cancellationToken);
        await DeleteStagedOutputAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// Decides what a set of timings is worth: held for the creator to review, or not usable at all.
    ///
    /// <para>
    /// Deliberately server-side and deliberately pure. The Function reports measurements and this
    /// makes the judgement, which means the rules can change without redeploying the slowest
    /// component in the system, and this method can be exercised in full with no Function, no Python
    /// runtime and no storage account.
    /// </para>
    ///
    /// <para>
    /// <b>It cannot return <see cref="SongLyricsStatus.Published"/>, and that is deliberate.</b>
    /// Publishing is the creator's decision, taken in the timing editor after they have heard the
    /// result against their own song. What this method still decides is whether the timings are
    /// <em>usable at all</em>: the structural checks below reject alignments that are broken rather
    /// than imprecise - timings that run backwards, or end after the track does - and no amount of
    /// listening turns those into something a player could use.
    /// </para>
    ///
    /// <para>
    /// The confidence no longer chooses anything. It graded the success message until it became clear
    /// the score is systematically pessimistic - it answers "did the aligner think it did well", not
    /// "are these timings good" - so the wording it produced told creators with excellent timings to
    /// expect work. Success now reads the same way every time and lets the creator judge by ear.
    /// </para>
    /// </summary>
    internal static LyricsClassification Classify(LyricsAlignmentResult result)
    {
        if (result.MatchedTokenCount == 0)
        {
            return new LyricsClassification(
                SongLyricsStatus.Failed,
                LyricsAlignmentFailureCodes.NoTokensMatched,
                "None of these words could be found in the song. Are these the right lyrics for this track?");
        }

        if (!result.IsMonotonic)
        {
            return new LyricsClassification(
                SongLyricsStatus.Failed,
                LyricsAlignmentFailureCodes.TimingsNotMonotonic,
                "The timings came back out of order, so they could not be used.");
        }

        // A little overshoot is normal - a final syllable can ring past the last decoded sample - so
        // this tolerates 5% rather than demanding the timings end inside the track exactly.
        //
        // The tolerance is shared with the editor's publish-time validator rather than written twice.
        // The two ask the same question at opposite ends of the feature, and if they disagreed the
        // result would be a document the pipeline accepted and the editor then refused to publish -
        // a song stuck in review with nothing visibly wrong with it.
        if (result.DurationMs > 0
            && result.LastWordEndMs > result.DurationMs * LyricsTimingsValidator.DurationOvershootTolerance)
        {
            return new LyricsClassification(
                SongLyricsStatus.Failed,
                LyricsAlignmentFailureCodes.TimingsExceedDuration,
                "The timings ran past the end of the track, so they could not be used.");
        }

        // Fewer than half the lines placed is not a confidence problem, it is a failed alignment
        // wearing a plausible score.
        if (result.LineCount > 0 && result.LinesWithTimingCount < result.LineCount / 2.0)
        {
            return new LyricsClassification(
                SongLyricsStatus.Failed,
                LyricsAlignmentFailureCodes.AlignmentFailed,
                "Most lines could not be placed in the song, so the timings could not be used.");
        }

        // NOTHING IS PUBLISHED HERE, at any confidence. Machine alignment of sung vocals lands
        // 150-300 ms out on a good day, and a listener notices that immediately, so the last word
        // belongs to the person whose song it is. The creator opens Preview Results, listens, taps
        // anything that drifts, and presses Publish.
        //
        // One message, with no score in it. The structural checks above are what "this run failed"
        // means; past them, the aligner's own opinion of itself adds nothing a creator can act on and
        // reads far worse than the timings deserve.
        return new LyricsClassification(
            SongLyricsStatus.NeedsReview,
            null,
            "We've timed these lyrics. Have a listen, and press Publish when you're happy - they "
            + "aren't visible to listeners yet.");
    }

    private async Task ApplyFailureAsync(
        AppDbContext context,
        LyricsAlignmentJob job,
        string failureCode,
        string message,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        job.Status = LyricsAlignmentJobStatus.Failed;
        job.Step = LyricsAlignmentStep.Failed;
        job.StepUpdatedAt = now;
        job.CompletedAt = now;
        job.FailureCode = Truncate(failureCode, 100);
        job.FailureMessage = Truncate(message, 2000);

        var lyrics = await context.SongLyrics
            .FirstOrDefaultAsync(row => row.SongMetadataId == job.SongMetadataId, cancellationToken);

        // Only a pending row moves to Failed. A song whose earlier alignment published good timings
        // keeps them - a failed re-run is a re-run that failed, not a reason to take away something
        // that already works.
        if (lyrics is not null && lyrics.Status == SongLyricsStatus.Pending)
        {
            lyrics.Status = SongLyricsStatus.Failed;
            lyrics.LastJobId = job.JobId;
            lyrics.UpdatedAt = now;
        }

        if (job.DurableFunctionTaskId is not null)
        {
            var task = await context.DurableFunctionTasks
                .FirstOrDefaultAsync(row => row.Id == job.DurableFunctionTaskId.Value, cancellationToken);

            if (task is not null && task.Status == DurableTaskStatus.Running)
            {
                task.Status = DurableTaskStatus.Failed;
                task.RuntimeStatusRaw = "Failed";
                task.FailureDetail = Truncate(message, 2000);
                task.CompletedAt = now;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyStepAsync(job.CreatorId, job.JobId, LyricsAlignmentStep.Failed, message);

        QueueCompletionEmail(job.JobId);

        _logger.LogWarning(
            "Lyrics alignment {JobId} failed: {FailureCode} - {Message}", job.JobId, failureCode, message);
    }

    private async Task AdvanceAsync(
        AppDbContext context,
        LyricsAlignmentJob job,
        LyricsAlignmentStep step,
        CancellationToken cancellationToken)
    {
        if (!LyricsAlignmentProgressCalculator.IsAdvance(job.Step, step))
        {
            return;
        }

        job.Step = step;
        job.Status = LyricsAlignmentJobStatus.Processing;
        job.StepUpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyStepAsync(job.CreatorId, job.JobId, step);
    }

    /// <summary>
    /// Sweeps the attempt's staging folder.
    ///
    /// <para>
    /// Best-effort throughout, and by folder prefix rather than by the paths the result reported -
    /// the same choice <c>SongUploadJobService.DeleteStagedBlobsAsync</c> makes, for the same reason:
    /// the Function decides its own output names, and deleting only what it told us about would leak
    /// anything it wrote that we did not ask for. The staging container's seven-day lifecycle rule is
    /// the backstop.
    /// </para>
    /// </summary>
    private async Task DeleteStagedOutputAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var staging = _containerFactory.GetUploadStagingContainer();
        if (staging is null)
        {
            return;
        }

        try
        {
            var prefix = $"{MediaProcessingStagingPaths.LyricsFolder(jobId)}/";

            await foreach (var blob in staging.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, prefix, cancellationToken))
            {
                await staging.GetBlobClient(blob.Name)
                    .DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up staged lyrics output for job {JobId}.", jobId);
        }
    }

    /// <summary>
    /// Ask Hangfire to tell the creator. Never sends inline.
    ///
    /// <para>
    /// Both callers run on the Function's terminal callback, which has a two-minute contract and a
    /// documented hazard if it overruns - the Function abandons the request and retries assembly on
    /// top of one still in flight. SMTP here is synchronous with a 30-second timeout, so it belongs
    /// on a background thread with its own retries rather than in that budget.
    /// </para>
    ///
    /// <para>
    /// Both call sites sit after their <c>SaveChangesAsync</c> and behind the already-terminal guards
    /// at the top of <c>CompleteAsync</c> and <c>FailAsync</c>, so a replayed callback returns before
    /// reaching either and cannot send a second email.
    /// </para>
    /// </summary>
    private void QueueCompletionEmail(Guid jobId)
    {
        try
        {
            _backgroundJobs.Enqueue<ILyricsAlignmentEmailService>(
                service => service.SendCompletionEmailAsync(jobId));
        }
        catch (Exception ex)
        {
            // Hangfire being unreachable must not fail an alignment that otherwise worked. The
            // creator still has the SignalR ping and the song list; they simply do not get the mail.
            _logger.LogWarning(ex, "Could not queue the completion email for lyrics job {JobId}.", jobId);
        }
    }

    private static string DescribeFailure(string failureCode) => failureCode switch
    {
        LyricsAlignmentFailureCodes.NoTokensMatched =>
            "None of these words could be found in the song. Are these the right lyrics for this track?",
        LyricsAlignmentFailureCodes.AudioBlobMissing =>
            "This song's audio could not be read.",
        LyricsAlignmentFailureCodes.LyricsBlobMissing =>
            "The submitted lyrics could not be read back.",
        LyricsAlignmentFailureCodes.SeparationFailed =>
            "We could not isolate the vocals on this track. Please try again.",
        LyricsAlignmentFailureCodes.LyricsTextEmpty =>
            "No lyrics were found to align.",
        _ => "We could not time these lyrics. Please try again."
    };

    private static string? Truncate(string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..maxLength];
}
