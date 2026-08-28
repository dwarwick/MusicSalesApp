#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Receives a finished packaging run from the Function and records it.
///
/// <para>
/// The Function reports facts; this app judges and writes — the same division the probe path uses.
/// What is different here is that one of those facts is a secret: the content key arrives in the
/// clear, over HTTPS, on a route gated by <c>X-Media-Processing-Key</c>, because the Function
/// generates it (it is the process running FFmpeg) and has no database to write it to.
/// </para>
/// </summary>
public interface IHlsPackagingResultHandler
{
    Task HandleAsync(AudioPackageResult result, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class HlsPackagingResultHandler : IHlsPackagingResultHandler
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IHlsContentKeyProtector _keyProtector;
    private readonly IHlsPackageSweeper _sweeper;
    private readonly ILogger<HlsPackagingResultHandler> _logger;

    public HlsPackagingResultHandler(
        IDbContextFactory<AppDbContext> contextFactory,
        IHlsContentKeyProtector keyProtector,
        IHlsPackageSweeper sweeper,
        ILogger<HlsPackagingResultHandler> logger)
    {
        _contextFactory = contextFactory;
        _keyProtector = keyProtector;
        _sweeper = sweeper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleAsync(AudioPackageResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var song = await context.SongMetadata
            .FirstOrDefaultAsync(s => s.Id == result.SongMetadataId, cancellationToken);

        if (song == null)
        {
            // Deleted while its packaging was in flight. The orphaned folder is swept rather than
            // left in storage forever, and the callback still succeeds so the queue does
            // not redeliver a message for a song that no longer exists.
            _logger.LogWarning(
                "Packaging finished for song {SongMetadataId}, which no longer exists. Sweeping {StreamId}.",
                result.SongMetadataId,
                result.HlsStreamId);

            await SweepAbandonedAsync(song, result.HlsStreamId, cancellationToken);
            await RecordBackfillOutcomeAsync(context, result, succeeded: false, "The song was deleted while it was being packaged.", cancellationToken);
            return;
        }

        if (result.Outcome != AudioProcessingOutcome.Playable)
        {
            _logger.LogWarning(
                "Packaging song {SongMetadataId} failed with {FailureCode}: {Diagnostic}",
                result.SongMetadataId,
                result.FailureCode,
                result.Diagnostic);

            // The song is untouched and keeps whatever package it already had. A failed repackage
            // must never take a working song off the air.
            await SweepAbandonedAsync(song, result.HlsStreamId, cancellationToken);
            await RecordBackfillOutcomeAsync(context, result, succeeded: false, result.Diagnostic ?? result.FailureCode, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.KeyHex) || string.IsNullOrWhiteSpace(result.IvHex))
        {
            _logger.LogError(
                "Packaging song {SongMetadataId} reported success without key material; refusing to record it.",
                result.SongMetadataId);

            await SweepAbandonedAsync(song, result.HlsStreamId, cancellationToken);
            await RecordBackfillOutcomeAsync(context, result, succeeded: false, "The packager reported success but sent no key.", cancellationToken);
            return;
        }

        // Idempotency: a redelivered callback for the package this song already has is a no-op.
        // Without this, a retry would sweep the very folder the row points at.
        if (song.HlsStreamId == result.HlsStreamId && !string.IsNullOrWhiteSpace(song.HlsKeyProtected))
        {
            _logger.LogInformation(
                "Packaging callback for song {SongMetadataId} repeats the package already recorded; ignoring.",
                result.SongMetadataId);

            await RecordBackfillOutcomeAsync(context, result, succeeded: true, null, cancellationToken);
            return;
        }

        var supersededStreamId = song.HlsStreamId;

        byte[] contentKey;
        try
        {
            contentKey = Convert.FromHexString(result.KeyHex);
        }
        catch (FormatException)
        {
            _logger.LogError(
                "Packaging song {SongMetadataId} reported a key that is not valid hex.",
                result.SongMetadataId);

            await SweepAbandonedAsync(song, result.HlsStreamId, cancellationToken);
            await RecordBackfillOutcomeAsync(context, result, succeeded: false, "The packager sent a malformed key.", cancellationToken);
            return;
        }

        song.HlsStreamId = result.HlsStreamId;
        song.HlsKeyProtected = _keyProtector.Protect(song.Id, contentKey);
        song.HlsIv = result.IvHex.ToLowerInvariant();
        song.HlsSegmentCount = result.SegmentCount;
        song.HlsTargetDurationSeconds = result.TargetDurationSeconds;
        song.HlsPackagedAt = DateTime.UtcNow;
        song.UpdatedAt = DateTime.UtcNow;

        await RecordBackfillOutcomeAsync(context, result, succeeded: true, null, cancellationToken);

        // Swept only after the row is committed pointing at the new folder. The other order would
        // leave a window where the old package is gone and the row still names it - which is exactly
        // the state the manifest endpoint reports as a 503.
        if (supersededStreamId.HasValue && supersededStreamId.Value != result.HlsStreamId)
        {
            await _sweeper.SweepAsync(supersededStreamId.Value, cancellationToken);
        }

        _logger.LogInformation(
            "Recorded encrypted HLS package {StreamId} ({SegmentCount} segments) for song {SongMetadataId}",
            result.HlsStreamId,
            result.SegmentCount,
            result.SongMetadataId);
    }

    /// <summary>
    /// Removes a package folder, unless the song's row is currently being served from it.
    ///
    /// <para>
    /// The guard is what stops a failure from taking a working song off the air. Queue delivery is
    /// at-least-once, so a message whose first attempt succeeded and was recorded can be redelivered;
    /// if that second attempt then fails, the failure callback names the very <c>HlsStreamId</c> the
    /// row now points at. Sweeping it unguarded would delete the live package and leave a perfectly
    /// healthy song answering 503 - the exact outcome the failure paths are written to avoid.
    /// </para>
    ///
    /// <para>
    /// A null song is the deleted-mid-flight case, where nothing points at the folder and sweeping
    /// is the whole point.
    /// </para>
    /// </summary>
    private async Task SweepAbandonedAsync(SongMetadata? song, Guid streamId, CancellationToken cancellationToken)
    {
        if (song is not null && song.HlsStreamId == streamId)
        {
            _logger.LogWarning(
                "Not sweeping HLS package {StreamId}: song {SongMetadataId} is currently served from it. "
                + "This is a redelivered message whose earlier attempt already succeeded.",
                streamId,
                song.Id);

            return;
        }

        await _sweeper.SweepAsync(streamId, cancellationToken);
    }

    /// <summary>
    /// Commits the song, then advances the backfill run this song belonged to, if any.
    ///
    /// <para>
    /// A run completes on its last callback rather than when its Hangfire job returns — the job only
    /// dispatches messages, and the work happens in Azure. That is the same shape the media-integrity
    /// audit uses.
    /// </para>
    ///
    /// <para>
    /// The song is committed <b>first and on its own</b>. If the run bookkeeping then fails, this
    /// method throws, the callback answers non-2xx, and the queue redelivers — and the redelivery
    /// lands on the idempotency branch above, which calls back in here and finishes the bookkeeping.
    /// The opposite order has no such recovery: it would leave a counted song unrecorded, and the
    /// retry would count it twice.
    /// </para>
    /// </summary>
    private async Task RecordBackfillOutcomeAsync(
        AppDbContext context,
        AudioPackageResult result,
        bool succeeded,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        await context.SaveChangesAsync(cancellationToken);

        if (result.BackfillRunId is not { } runId)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var processingSeconds = Math.Max(0, result.ProcessingSeconds);

        // Incremented BY THE DATABASE, not read-modify-written here.
        //
        // Up to MaxInFlightMessages callbacks are in flight at once and they land on whatever web
        // instance happens to serve each one, so two of them routinely load the same row, each add
        // one to the value they read, and one increment is lost. That is not merely a cosmetic
        // miscount: a run completes when DispatchedCount - Succeeded - Failed reaches zero, so one
        // lost increment means it never reaches it. The run then sits in AwaitingCallbacks forever
        // still holding ActiveLockKey - and since StartAsync refuses to start while any run holds
        // that lock, no future run could ever be started without someone editing the database.
        var affected = succeeded
            ? await context.HlsPackagingBackfillRuns
                .Where(run => run.Id == runId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(run => run.SucceededCount, run => run.SucceededCount + 1)

                        // Only successes contribute: a failed song's time says nothing about how long
                        // packaging one takes, and would drag the per-song average toward whatever
                        // the failure happened to cost.
                        .SetProperty(run => run.TotalProcessingSeconds, run => run.TotalProcessingSeconds + processingSeconds)
                        .SetProperty(run => run.LastCallbackAt, now),
                    cancellationToken)
            : await context.HlsPackagingBackfillRuns
                .Where(run => run.Id == runId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(run => run.FailedCount, run => run.FailedCount + 1)
                        .SetProperty(run => run.LastCallbackAt, now),
                    cancellationToken);

        if (affected == 0)
        {
            // The run was deleted while its messages were still being processed. The song's own
            // package is recorded either way, which is what actually matters.
            return;
        }

        if (!succeeded)
        {
            await RecordFailureRowAsync(context, runId, result, failureReason, cancellationToken);
        }

        await TryCompleteAsync(context, runId, now, cancellationToken);
    }

    /// <summary>
    /// Records one failed song for the admin page's failure table, up to the per-run cap.
    ///
    /// <para>
    /// Bounded so one systemic failure - a misconfigured queue, a Function that cannot start -
    /// cannot write a row per song in the catalogue and turn a diagnostic aid into a second
    /// incident. The counters stay exact regardless; only the detail is capped.
    /// </para>
    /// </summary>
    private static async Task RecordFailureRowAsync(
        AppDbContext context,
        int runId,
        AudioPackageResult result,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        var recorded = await context.HlsPackagingBackfillFailures
            .CountAsync(failure => failure.RunId == runId, cancellationToken);

        if (recorded >= HlsPackagingBackfillRun.MaxRecordedFailures)
        {
            return;
        }

        context.HlsPackagingBackfillFailures.Add(new HlsPackagingBackfillFailure
        {
            RunId = runId,
            SongMetadataId = result.SongMetadataId,
            FailureCode = Truncate(result.FailureCode, 100),
            Reason = Truncate(failureReason, 1000),
            OccurredAt = DateTime.UtcNow
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Finishes the run if this was its last outstanding callback.
    ///
    /// <para>
    /// Decided here rather than in the Hangfire job because the job only dispatched messages. The
    /// status is checked as part of the UPDATE rather than before it, so two callbacks arriving at
    /// the same moment cannot both claim the completion, and a run an operator has since cancelled
    /// is not quietly resurrected into Completed.
    /// </para>
    ///
    /// <para>
    /// The zero check only counts once dispatch has finished: while the job is still enqueueing, an
    /// outstanding count of zero just means the callbacks are keeping up with it.
    /// </para>
    /// </summary>
    private async Task TryCompleteAsync(
        AppDbContext context,
        int runId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var run = await context.HlsPackagingBackfillRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);

        if (run is not { Status: HlsPackagingBackfillStatus.AwaitingCallbacks } || run.OutstandingCount > 0)
        {
            return;
        }

        var completed = await context.HlsPackagingBackfillRuns
            .Where(candidate => candidate.Id == runId
                && candidate.Status == HlsPackagingBackfillStatus.AwaitingCallbacks)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, HlsPackagingBackfillStatus.Completed)
                    .SetProperty(candidate => candidate.CompletedAt, now)

                    // Releasing the lock is what lets the next run start.
                    .SetProperty(candidate => candidate.ActiveLockKey, (int?)null),
                cancellationToken);

        if (completed > 0)
        {
            _logger.LogInformation(
                "HLS packaging run {RunId} completed: {Succeeded} succeeded, {Failed} failed.",
                runId,
                run.SucceededCount,
                run.FailedCount);
        }
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
