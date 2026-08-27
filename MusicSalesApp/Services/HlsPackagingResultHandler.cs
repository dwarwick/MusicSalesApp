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

            await _sweeper.SweepAsync(result.HlsStreamId, cancellationToken);
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
            await _sweeper.SweepAsync(result.HlsStreamId, cancellationToken);
            await RecordBackfillOutcomeAsync(context, result, succeeded: false, result.Diagnostic ?? result.FailureCode, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.KeyHex) || string.IsNullOrWhiteSpace(result.IvHex))
        {
            _logger.LogError(
                "Packaging song {SongMetadataId} reported success without key material; refusing to record it.",
                result.SongMetadataId);

            await _sweeper.SweepAsync(result.HlsStreamId, cancellationToken);
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

            await _sweeper.SweepAsync(result.HlsStreamId, cancellationToken);
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
    /// Advances the backfill run this song belonged to, if any, and saves everything in one write.
    ///
    /// <para>
    /// A run completes on its last callback rather than when its Hangfire job returns — the job only
    /// dispatches messages, and the work happens in Azure. That is the same shape the media-integrity
    /// audit uses.
    /// </para>
    /// </summary>
    private async Task RecordBackfillOutcomeAsync(
        AppDbContext context,
        AudioPackageResult result,
        bool succeeded,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        if (result.BackfillRunId is { } runId)
        {
            var run = await context.HlsPackagingBackfillRuns
                .Include(r => r.Failures)
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

            if (run != null)
            {
                if (succeeded)
                {
                    run.SucceededCount++;

                    // Only successes contribute: a failed song's time says nothing about how long
                    // packaging one takes, and would drag the per-song average toward whatever the
                    // failure happened to cost.
                    run.TotalProcessingSeconds += Math.Max(0, result.ProcessingSeconds);
                }
                else
                {
                    run.FailedCount++;

                    // Bounded so one systemic failure cannot write a row per song in the catalogue.
                    if (run.Failures.Count < HlsPackagingBackfillRun.MaxRecordedFailures)
                    {
                        run.Failures.Add(new HlsPackagingBackfillFailure
                        {
                            SongMetadataId = result.SongMetadataId,
                            FailureCode = Truncate(result.FailureCode, 100),
                            Reason = Truncate(failureReason, 1000),
                            OccurredAt = DateTime.UtcNow
                        });
                    }
                }

                run.LastCallbackAt = DateTime.UtcNow;

                // A run finishes on its last callback, not when its Hangfire job returns - the job
                // only dispatched messages. Completion is therefore decided here, and only once
                // dispatch has actually finished: while the job is still enqueueing, an outstanding
                // count of zero just means the callbacks are keeping up with it.
                if (run.Status == HlsPackagingBackfillStatus.AwaitingCallbacks && run.OutstandingCount == 0)
                {
                    run.Status = HlsPackagingBackfillStatus.Completed;
                    run.CompletedAt = DateTime.UtcNow;
                    run.ActiveLockKey = null;

                    _logger.LogInformation(
                        "HLS packaging run {RunId} completed: {Succeeded} succeeded, {Failed} failed.",
                        run.Id,
                        run.SucceededCount,
                        run.FailedCount);
                }
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
