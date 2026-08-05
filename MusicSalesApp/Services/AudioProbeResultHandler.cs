#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Applies the result of one decode probe performed by the Azure Function.
///
/// <para>
/// The Function reports raw facts only - blob properties, detected container, decoded duration and
/// a three-way playable/unplayable/inconclusive verdict. Every judgement built on top of those
/// (what counts as healthy, what is merely a naming warning, when to quarantine) stays here, so the
/// audit's rules can change without redeploying the Function.
/// </para>
/// </summary>
public interface IAudioProbeResultHandler
{
    Task HandleAsync(AudioProbeResult result, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AudioProbeResultHandler : IAudioProbeResultHandler
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IAzureStorageService _storage;
    private readonly IMediaProcessingQueueClient _queueClient;
    private readonly IMediaIntegrityAuditService _auditService;
    private readonly ILogger<AudioProbeResultHandler> _logger;

    public AudioProbeResultHandler(
        IDbContextFactory<AppDbContext> contextFactory,
        IAzureStorageService storage,
        IMediaProcessingQueueClient queueClient,
        IMediaIntegrityAuditService auditService,
        ILogger<AudioProbeResultHandler> logger)
    {
        _contextFactory = contextFactory;
        _storage = storage;
        _queueClient = queueClient;
        _auditService = auditService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task HandleAsync(AudioProbeResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Kind switch
        {
            AudioProbeKind.TrackLengthRepair => HandleTrackLengthRepairAsync(result, cancellationToken),
            AudioProbeKind.MediaIntegrityAudit => HandleAuditAsync(result, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task HandleTrackLengthRepairAsync(AudioProbeResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome != AudioProcessingOutcome.Playable
            || !result.DurationSeconds.HasValue
            || result.DurationSeconds.Value <= 0)
        {
            _logger.LogWarning(
                "Track length repair skipped for song {SongId}: {Outcome} {FailureCode}",
                result.SongMetadataId,
                result.Outcome,
                result.FailureCode);
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var song = await context.SongMetadata
            .Where(item => item.IsActive && !item.IsAlbumCover)
            .FirstOrDefaultAsync(item => item.Id == result.SongMetadataId, cancellationToken);

        if (song is null)
        {
            _logger.LogInformation(
                "Track length repair skipped for song {SongId}: no longer an active playable track.",
                result.SongMetadataId);
            return;
        }

        if (song.TrackLength.HasValue)
        {
            // Something else populated it between dispatch and now - a re-upload, or an audit
            // repair. Leave it alone.
            return;
        }

        song.TrackLength = result.DurationSeconds.Value;
        if (string.IsNullOrEmpty(song.Mp3BlobPath))
        {
            song.Mp3BlobPath = result.BlobPath;
        }

        song.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Updated missing track length for song {SongId} to {TrackLengthSeconds} seconds",
            song.Id,
            result.DurationSeconds.Value);
    }

    private async Task HandleAuditAsync(AudioProbeResult result, CancellationToken cancellationToken)
    {
        if (!result.AuditRunId.HasValue)
        {
            _logger.LogWarning("Audit probe result for song {SongId} carried no run id", result.SongMetadataId);
            return;
        }

        var runId = result.AuditRunId.Value;

        SongMetadata? song;
        MediaAuditMode mode;
        await using (var readContext = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var run = await readContext.MediaIntegrityAuditRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == runId, cancellationToken);
            if (run is null || run.Status == MediaAuditRunStatus.Completed)
            {
                return;
            }

            mode = run.Mode;

            // An item already present means this song has been recorded - a duplicate delivery, or
            // a queue retry after the response was lost. Recording it twice would double-count the
            // run and could quarantine the same song twice.
            var alreadyRecorded = await readContext.MediaIntegrityAuditItems.AnyAsync(
                item => item.AuditRunId == runId && item.SongMetadataId == result.SongMetadataId,
                cancellationToken);
            if (alreadyRecorded)
            {
                return;
            }

            song = await readContext.SongMetadata.AsNoTracking()
                .Include(item => item.Creator).ThenInclude(creator => creator.User)
                .FirstOrDefaultAsync(item => item.Id == result.SongMetadataId, cancellationToken);
        }

        if (song is null || string.IsNullOrWhiteSpace(song.Mp3BlobPath))
        {
            // Deleted between dispatch and now. It still has to count towards the run, or the run
            // would never reach its candidate count and would sit Running forever.
            await _auditService.RecordSkippedItemAsync(runId, result.SongMetadataId, cancellationToken);
            return;
        }

        var item = await BuildItemAsync(runId, song, result, cancellationToken);

        // A single failed decode is not enough to condemn a song. Re-probe once against a fresh
        // Azure stream; only a second failure is recorded, and a second success downgrades the
        // verdict to Inconclusive rather than leaving a scary confirmed failure on the report.
        if (item.Outcome == MediaAuditOutcome.ConfirmedUnplayable && result.Attempt < 2)
        {
            try
            {
                await _queueClient.EnqueueProbesAsync(
                    new[]
                    {
                        new AudioProbeRequest
                        {
                            ProbeId = Guid.NewGuid(),
                            Kind = AudioProbeKind.MediaIntegrityAudit,
                            AuditRunId = runId,
                            SongMetadataId = song.Id,
                            BlobPath = song.Mp3BlobPath,
                            Attempt = 2
                        }
                    },
                    cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                // The re-probe is the only thing that would have counted this song, so returning
                // here would drop it from the run entirely and leave ProcessedCount short of
                // CandidateCount forever. Record it instead - as Inconclusive, not as a confirmed
                // failure, because the whole point of the second attempt is that one bad decode is
                // not evidence enough to quarantine a song.
                _logger.LogWarning(
                    ex,
                    "Could not enqueue the confirmation re-probe for song {SongId} in audit {RunId}; "
                        + "recording the first attempt as inconclusive",
                    song.Id,
                    runId);

                item.Outcome = MediaAuditOutcome.Inconclusive;
                item.FailureCode = "ConfirmationNotAttempted";
                item.Diagnostic =
                    "A first decode failed, but the confirming re-probe could not be queued, so the "
                    + "failure was never verified. Re-run the audit for this song.";
            }
        }

        if (result.Attempt >= 2 && item.Outcome != MediaAuditOutcome.ConfirmedUnplayable)
        {
            item.Outcome = MediaAuditOutcome.Inconclusive;
            item.FailureCode = "FailureNotReproduced";
            item.Diagnostic = "The suspected failure was not reproduced on a fresh Azure stream.";
        }

        item.Attempts = result.Attempt;

        await _auditService.RecordProbedItemAsync(item, mode, cancellationToken);
    }

    private async Task<MediaIntegrityAuditItem> BuildItemAsync(
        int runId,
        SongMetadata song,
        AudioProbeResult result,
        CancellationToken cancellationToken)
    {
        var item = new MediaIntegrityAuditItem
        {
            AuditRunId = runId,
            SongMetadataId = song.Id,
            EffectiveTitle = SongTitleHelper.GetEffectiveTitle(song.SongTitle, song.Mp3BlobPath, song.BlobPath),
            PlaybackBlobPath = song.Mp3BlobPath,
            OriginalAudioBlobPath = song.OriginalAudioBlobPath,
            Attempts = result.Attempt,
            CheckedAt = DateTime.UtcNow,
            BlobLength = result.BlobLength,
            ContentType = result.ContentType,
            ETag = result.ETag,
            BlobLastModified = result.BlobLastModified,
            DetectedFormat = result.DetectedFormat,
            DecodedDuration = result.DurationSeconds
        };

        // A cheap HEAD against Azure, and no FFmpeg, so it stays on this side rather than being
        // bundled into the probe message.
        await PopulateOriginalSourceEvidenceAsync(item, song, cancellationToken);

        if (!result.BlobExists)
        {
            return Confirmed(item, "PlaybackBlobMissing", "The registered playback blob does not exist.");
        }

        if (result.Outcome == AudioProcessingOutcome.Inconclusive)
        {
            item.Outcome = MediaAuditOutcome.Inconclusive;
            item.FailureCode = result.FailureCode ?? "DecoderInfrastructureFailure";
            item.Diagnostic = result.Diagnostic;
            return item;
        }

        if (result.Outcome != AudioProcessingOutcome.Playable
            || !result.DurationSeconds.HasValue
            || result.DurationSeconds <= 0)
        {
            return Confirmed(
                item,
                result.FailureCode ?? "DecoderRejected",
                result.Diagnostic ?? "FFmpeg could not completely decode an audio stream with positive duration.");
        }

        var metadataMissing = string.IsNullOrWhiteSpace(song.SongTitle)
            || !song.TrackLength.HasValue || song.TrackLength <= 0;

        // Filenames are no longer constrained, so only the container and content type are worth
        // warning about here.
        var containerWarning =
            !string.Equals(result.DetectedFormat, ".mp3", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result.ContentType, "audio/mpeg", StringComparison.OrdinalIgnoreCase);

        item.Outcome = metadataMissing
            ? MediaAuditOutcome.MetadataRepairable
            : containerWarning
                ? MediaAuditOutcome.NamingWarning
                : item.IsOriginalSourceMissing
                    ? MediaAuditOutcome.OriginalSourceMissing
                    : MediaAuditOutcome.Healthy;

        if (item.IsOriginalSourceMissing)
        {
            item.FailureCode = "OriginalSourceMissing";
            item.Diagnostic = string.IsNullOrWhiteSpace(song.OriginalAudioBlobPath)
                ? "This legacy record has no retained original source registered."
                : "The retained original source path is registered but the blob is missing.";
        }

        return item;
    }

    private async Task PopulateOriginalSourceEvidenceAsync(
        MediaIntegrityAuditItem item,
        SongMetadata song,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(song.OriginalAudioBlobPath))
        {
            item.IsOriginalSourceMissing = true;
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.IsOriginalSourceCheckInconclusive = false;
            item.IsOriginalSourceMissing = !await _storage.ExistsAsync(song.OriginalAudioBlobPath);
        }
        catch (Exception ex)
        {
            item.IsOriginalSourceCheckInconclusive = true;
            _logger.LogWarning(
                ex,
                "Unable to verify original source {OriginalPath} for song {SongId}",
                song.OriginalAudioBlobPath,
                song.Id);
        }
    }

    private static MediaIntegrityAuditItem Confirmed(
        MediaIntegrityAuditItem item,
        string code,
        string diagnostic)
    {
        item.Outcome = MediaAuditOutcome.ConfirmedUnplayable;
        item.FailureCode = code;
        item.Diagnostic = diagnostic;
        return item;
    }
}
