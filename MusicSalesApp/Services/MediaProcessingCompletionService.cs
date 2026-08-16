#nullable enable
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Turns a finished transcode into a live song.
/// </summary>
public interface IMediaProcessingCompletionService
{
    /// <summary>
    /// Assembles the song: copies the staged blobs into the media container, writes the
    /// <see cref="SongMetadata"/> row and generates the derived artwork.
    ///
    /// <para>
    /// Idempotent. The queue retries the terminal callback on any non-2xx, and the poison path can
    /// replay it too, so a second call for an already-completed job must be a no-op rather than a
    /// second song.
    /// </para>
    /// </summary>
    Task CompleteAsync(AudioTranscodeResult result, CancellationToken cancellationToken = default);

    /// <summary>Records that processing failed and tells the creator why.</summary>
    Task FailAsync(
        Guid jobId,
        string failureCode,
        string diagnostic,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class MediaProcessingCompletionService : IMediaProcessingCompletionService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IBlobContainerFactory _containerFactory;
    private readonly ISongMetadataService _metadataService;
    private readonly IOpenGraphService _openGraphService;
    private readonly ISongUploadJobService _jobService;
    private readonly IUploadProgressNotifier _progressNotifier;
    private readonly IStagingToMediaCopier _copier;
    private readonly IOptions<MediaProcessingOptions> _options;
    private readonly ILogger<MediaProcessingCompletionService> _logger;

    public MediaProcessingCompletionService(
        IDbContextFactory<AppDbContext> contextFactory,
        IBlobContainerFactory containerFactory,
        ISongMetadataService metadataService,
        IOpenGraphService openGraphService,
        ISongUploadJobService jobService,
        IUploadProgressNotifier progressNotifier,
        IStagingToMediaCopier copier,
        IOptions<MediaProcessingOptions> options,
        ILogger<MediaProcessingCompletionService> logger)
    {
        _contextFactory = contextFactory;
        _containerFactory = containerFactory;
        _metadataService = metadataService;
        _openGraphService = openGraphService;
        _jobService = jobService;
        _progressNotifier = progressNotifier;
        _copier = copier;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(AudioTranscodeResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var job = await LoadJobAsync(result.JobId, cancellationToken);
        if (job is null)
        {
            _logger.LogWarning("Completion callback for unknown job {JobId}", result.JobId);
            return;
        }

        if (job.Status is SongUploadJobStatus.Completed or SongUploadJobStatus.Failed)
        {
            // The retry path, not an error. Say nothing and return 200.
            //
            // Failed has to be caught here as well as Completed. The reconciler declares a job dead
            // on a stale StepUpdatedAt, and that timestamp is only refreshed by progress pings -
            // which are fire-and-forget and swallow their own failures, so a site restart makes a
            // perfectly healthy Function look stalled. When that Function then finishes and posts a
            // Playable result, this used to fall through into a full assembly of a job the creator
            // has already been told failed: either publishing it anyway behind their back, or - once
            // FailAsync had deleted the staged source - throwing on the copy and burning every queue
            // redelivery until the message poisoned.
            _logger.LogInformation(
                "Completion callback replayed for already-{Status} job {JobId}", job.Status, result.JobId);

            // Whatever the Function wrote after the job was failed is unreferenced: staging may have
            // been recreated by a retry that finished late, and any cover-art renditions were
            // written straight into the media container before the row existed. Neither is swept by
            // anything else - the media account has no lifecycle rule, and could not safely be given
            // one, because its prefixes are the live catalogue.
            await _jobService.DeleteStagedBlobsAsync(job, cancellationToken);
            if (job.Status == SongUploadJobStatus.Failed)
            {
                await DeleteOrphanedRenditionsAsync(job, cancellationToken);
            }

            return;
        }

        if (result.Outcome != AudioProcessingOutcome.Playable)
        {
            await FailAsync(result.JobId, result.FailureCode, result.Diagnostic, cancellationToken);
            return;
        }

        if (!result.DurationSeconds.HasValue || result.DurationSeconds.Value <= 0)
        {
            await FailAsync(
                result.JobId,
                MediaProcessingFailureCodes.ZeroDuration,
                "The processed audio reported no playable duration.",
                cancellationToken);
            return;
        }

        var staging = _containerFactory.GetUploadStagingContainer()
            ?? throw new InvalidOperationException("Upload staging is not configured.");
        var media = _containerFactory.GetMediaContainer();

        var mediaGuid = job.MediaGuid;
        var mp3Path = SongMediaPaths.Playback(mediaGuid);
        var originalPath = SongMediaPaths.OriginalAudio(mediaGuid, job.SourceExtension);
        var imagePath = string.IsNullOrWhiteSpace(job.CoverArtExtension)
            ? null
            : SongMediaPaths.CoverArt(mediaGuid, job.CoverArtExtension);
        var originalImagePath = string.IsNullOrWhiteSpace(job.CoverArtExtension)
            ? null
            : SongMediaPaths.OriginalCoverArt(mediaGuid, job.CoverArtExtension);

        // The Function is sitting on an open HTTP request for the whole of what follows, and it stops
        // waiting at MediaProcessingTimeouts.TerminalCallback. Bounding assembly one notch below that
        // means a stuck copy fails here, cleanly, while the Function is still listening - instead of
        // running on invisibly after the Function has given up and the queue has redelivered.
        using var assemblyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        assemblyTimeout.CancelAfter(MediaProcessingTimeouts.Assembly);
        var assemblyToken = assemblyTimeout.Token;

        var copied = new List<string>();
        try
        {
            await AdvanceAsync(job, AudioProcessingStep.Copying, assemblyToken);

            // Staging and media are on different storage accounts - media is Premium, which has no
            // Queue service, so staging had to go elsewhere. That makes every copy below a
            // cross-account copy needing a read SAS over the source, and a real data movement
            // rather than the metadata operation a same-account copy would be.
            var sasQuery = _copier.CreateStagingReadSasQuery(staging);

            var playbackSource = string.IsNullOrWhiteSpace(result.PlaybackBlobPath)
                ? MediaProcessingStagingPaths.Playback(mediaGuid)
                : result.PlaybackBlobPath;

            if (result.SourceWasAlreadyMp3 || string.Equals(originalPath, mp3Path, StringComparison.OrdinalIgnoreCase))
            {
                // SongMediaPaths.OriginalAudio collapses onto the playback path for an MP3 source,
                // so the upload is both the original and the playback copy and is stored once.
                await _copier.CopyAsync(
                    staging, job.SourceBlobPath, media, mp3Path, sasQuery,
                    MediaProcessingTimeouts.Assembly, assemblyToken, cancellationToken);
                copied.Add(mp3Path);
            }
            else
            {
                await _copier.CopyAsync(
                    staging, job.SourceBlobPath, media, originalPath, sasQuery,
                    MediaProcessingTimeouts.Assembly, assemblyToken, cancellationToken);
                copied.Add(originalPath);

                await _copier.CopyAsync(
                    staging, playbackSource, media, mp3Path, sasQuery,
                    MediaProcessingTimeouts.Assembly, assemblyToken, cancellationToken);
                copied.Add(mp3Path);
            }

            // Everything the metadata could use to claim this song has artwork, cleared together.
            // Nulling only the two paths left OriginalCoverArtFileName and CoverArtVariantWidths
            // still being written - and an empty width CSV does not mean "no art", it means "art we
            // could not render, serve the full-size master", a master that was never copied. Any
            // consumer keying off the filename or the non-null CSV - the media-integrity audit, the
            // admin art-replace screen - would see a song claiming artwork the container has not got.
            var coverArtFileName = job.CoverArtFileName;
            var coverArtVariantWidths = result.CoverArtVariantWidths;

            if (!CoverArtIsUsable(result.CoverArtDiagnosticCode))
            {
                _logger.LogWarning(
                    "Cover art for job {JobId} could not be decoded; publishing the song without artwork.",
                    mediaGuid);

                imagePath = null;
                originalImagePath = null;
                coverArtFileName = null;
                coverArtVariantWidths = null;
            }

            if (imagePath is not null && originalImagePath is not null && !string.IsNullOrWhiteSpace(job.CoverArtBlobPath))
            {
                await _copier.CopyAsync(
                    staging, job.CoverArtBlobPath, media, imagePath, sasQuery,
                    MediaProcessingTimeouts.Assembly, assemblyToken, cancellationToken);
                copied.Add(imagePath);

                // The served copy and the retained original start identical; they diverge the first
                // time the art is cropped, which only rewrites the served copy.
                await _copier.CopyAsync(
                    staging, job.CoverArtBlobPath, media, originalImagePath, sasQuery,
                    MediaProcessingTimeouts.Assembly, assemblyToken, cancellationToken);
                copied.Add(originalImagePath);
            }

            await AdvanceAsync(job, AudioProcessingStep.SavingMetadata, assemblyToken);

            var savedMetadata = await _metadataService.UpsertValidatedUploadAsync(new SongMetadata
            {
                MediaGuid = mediaGuid,
                BlobPath = mp3Path,
                Mp3BlobPath = mp3Path,
                ImageBlobPath = imagePath,
                OriginalAudioBlobPath = originalPath,
                OriginalAudioFileName = job.SourceFileName,
                OriginalAudioFileSize = job.SourceFileSize,
                OriginalAudioContentType = job.SourceContentType,
                OriginalCoverArtBlobPath = originalImagePath,
                OriginalCoverArtFileName = coverArtFileName,

                // The Function already wrote these renditions into the GUID folder, straight from
                // the bitmap it had decoded. Recording the width set here rather than re-deriving it
                // saves a download from the Premium account, a second decode, four re-encodes and a
                // second database round trip - all inside the assembly budget the Function is
                // holding an HTTP request open for.
                //
                // Null and empty mean different things: null is "no cover art", empty is "art we
                // could not render", which serves the full-size master. Version is set explicitly
                // because the coordinator call this replaced incremented 0 -> 1 on every upload, and
                // CoverArtUrlBuilder emits the value unconditionally as ?v=.
                CoverArtVariantWidths = coverArtVariantWidths is null
                    ? null
                    : ImageVariantSizes.ToCsv(coverArtVariantWidths),
                CoverArtVariantVersion = 1,

                FileExtension = ".mp3",
                SongTitle = job.SongTitle,
                AlbumName = job.AlbumName ?? string.Empty,
                IsAlbumCover = false,
                TrackLength = result.DurationSeconds.Value,
                CreatorId = job.CreatorId
            });

            // The metadata commit succeeded, so the blobs are live and referenced. Nothing below
            // this point should undo the copies.
            copied.Clear();

            await AdvanceAsync(job, AudioProcessingStep.GeneratingArtwork, assemblyToken);

            if (imagePath is not null)
            {
                try
                {
                    // Nothing to invalidate: the GUID folder was created moments ago, so no sharing
                    // image can already exist for it.
                    await _openGraphService.PreGenerateFacebookImageAsync(imagePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to pre-generate sharing image for {Path}", imagePath);
                }

                // Renditions are NOT regenerated here. The Function wrote them before this callback,
                // and their widths were recorded above. Calling the coordinator "for safety" would
                // re-download the master from the Premium account, re-decode it, re-encode every
                // rung and overwrite the Function's identical bytes - doubling the exact work this
                // moved off the web server, inside the assembly budget.
                //
                // An empty width set means the art could not be rendered. The song still publishes
                // and serves its full-size master; ImageVariantBackfillService is what sweeps songs
                // with no recorded widths, so there is nothing to retry inline.
                if (result.CoverArtVariantWidths is { Count: 0 })
                {
                    _logger.LogWarning(
                        "Song {SongMetadataId} published with no cover-art renditions ({DiagnosticCode}); "
                        + "it will serve its full-size master until the backfill regenerates them",
                        savedMetadata.Id,
                        result.CoverArtDiagnosticCode);
                }
            }

            await MarkCompletedAsync(job, savedMetadata.Id, result, assemblyToken);
            await _jobService.DeleteStagedBlobsAsync(job, assemblyToken);

            _logger.LogInformation(
                "Assembled song {SongMetadataId} from upload job {JobId}",
                savedMetadata.Id,
                mediaGuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assembly failed for upload job {JobId}; leaving it retryable", mediaGuid);

            // Unwind only what this attempt wrote into the media container. The destination folder
            // is a GUID nothing else can be writing to, so deleting is enough - no snapshot ledger.
            // Not the request token: a caller that timed out or hung up is exactly the case that
            // leaves half-copied blobs behind, and passing an already-cancelled token here would skip
            // every delete.
            foreach (var path in copied)
            {
                try
                {
                    await media.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogError(cleanupException, "Could not clean up {Path} after a failed assembly", path);
                }
            }

            // Deliberately NOT marked Failed, and deliberately NOT deleting staging.
            //
            // Everything that lands here - a storage blip, a database hiccup, the caller's request
            // being aborted - is transient, and the transcode itself already succeeded. Failing the
            // job would delete the staged source, and the rethrow below makes the queue redeliver;
            // the retry would then find its source gone and 404 forever until the message poisoned,
            // losing a song that was only ever one retry away from succeeding.
            //
            // Leaving the job Processing with its staging intact means the redelivery re-runs
            // assembly cleanly. If every attempt fails, the reconciler is what eventually fails the
            // job - which is the right place for that decision, because it can tell the difference
            // between "busy" and "stuck".
            throw;
        }
    }

    /// <inheritdoc />
    public async Task FailAsync(
        Guid jobId,
        string failureCode,
        string diagnostic,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.SongUploadJobs.FirstOrDefaultAsync(j => j.MediaGuid == jobId, cancellationToken);
        if (job is null)
        {
            _logger.LogWarning("Failure callback for unknown job {JobId}", jobId);
            return;
        }

        if (job.Status is SongUploadJobStatus.Completed or SongUploadJobStatus.Failed)
        {
            return;
        }

        // Captured before Step is overwritten: the bar stops where the song actually got to rather
        // than snapping anywhere, so the creator can see how far it made it before it gave up.
        var reachedStep = job.Step;

        job.Status = SongUploadJobStatus.Failed;
        job.Step = AudioProcessingStep.Failed;
        job.StepUpdatedAt = DateTime.UtcNow;
        job.CompletedAt = DateTime.UtcNow;
        job.FailureCode = Truncate(failureCode, 100);
        job.FailureMessage = Truncate(diagnostic, 2000);
        await context.SaveChangesAsync(cancellationToken);

        await _progressNotifier.NotifyAsync(
            job.CreatorId,
            new AudioProcessingProgress
            {
                JobId = jobId,
                Step = AudioProcessingStep.Failed,
                OverallPercent = AudioProcessingProgressCalculator.ToOverallPercent(reachedStep),
                Detail = job.FailureMessage
            },
            cancellationToken);

        await _jobService.DeleteStagedBlobsAsync(job, cancellationToken);
        await DeleteOrphanedRenditionsAsync(job, cancellationToken);
    }

    /// <summary>
    /// Removes cover-art renditions the Function may have written for a job that will never publish.
    ///
    /// <para>
    /// The Function writes renditions straight into the song's GUID folder in the media container,
    /// before the master is copied in and before the <c>SongMetadata</c> row exists. That is safe
    /// while the job is alive - the public whitelist resolves a rendition back to its master and
    /// finds no row, so nothing is reachable - but once the job is terminally failed those blobs
    /// have no owner and nothing else will ever remove them. Staging has a 7-day lifecycle rule;
    /// the media account deliberately has none, because its prefixes are the live catalogue.
    /// </para>
    ///
    /// <para>
    /// Best effort throughout, and only ever touches paths derived from this job's own GUID. A song
    /// that published normally never reaches here, so this cannot delete a live rendition.
    /// </para>
    /// </summary>
    private async Task DeleteOrphanedRenditionsAsync(SongUploadJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.CoverArtExtension))
        {
            return;
        }

        try
        {
            var media = _containerFactory.GetMediaContainer();
            if (media is null)
            {
                return;
            }

            var master = SongMediaPaths.CoverArt(job.MediaGuid, job.CoverArtExtension);

            // The full ladder rather than a recorded width set: the row that would have recorded it
            // was never written, and an attempt that failed part-way may have left any subset.
            foreach (var path in ImageVariantPaths.VariantsFor(master, ImageVariantSizes.CoverArt))
            {
                try
                {
                    // Not the request token. A caller that timed out or hung up is exactly the case
                    // that leaves renditions behind, and an already-cancelled token would skip every
                    // delete - the same reasoning the assembly rollback uses.
                    await media.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex, "Could not delete orphaned rendition {Path} for job {JobId}", path, job.MediaGuid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not clean up orphaned renditions for job {JobId}", job.MediaGuid);
        }
    }

    /// <summary>
    /// Whether the cover art the Function looked at is worth copying into the catalogue.
    ///
    /// <para>
    /// Only a decode failure disqualifies it. The Function reports that when SkiaSharp cannot read
    /// the image at all, and copying it anyway would set <c>ImageBlobPath</c> to a master no browser
    /// can render - a song showing a permanently broken image, and a sharing-image generation that
    /// fails behind it. Publishing audio-only gives the creator the default artwork and a song that
    /// works, which they can fix by uploading new art.
    /// </para>
    ///
    /// <para>
    /// Every other diagnostic means the image decoded but its renditions did not all get written -
    /// <c>upload_failed</c>, <c>encode_failed</c>. Those songs serve their full-size master
    /// perfectly well and must still get their artwork.
    /// </para>
    ///
    /// <para>
    /// Today the web app decode-validates before staging, so this is nearly unreachable. It exists
    /// because that check is what moves to the Function once uploads go directly to storage, at
    /// which point this is the only thing standing between a renamed <c>.txt</c> and a broken
    /// catalogue entry.
    /// </para>
    /// </summary>
    internal static bool CoverArtIsUsable(string diagnosticCode)
        => !string.Equals(
            diagnosticCode,
            ImageVariantFailureCodes.DecodeFailed,
            StringComparison.Ordinal);

    private async Task<SongUploadJob?> LoadJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.SongUploadJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(job => job.MediaGuid == jobId, cancellationToken);
    }

    private async Task AdvanceAsync(
        SongUploadJob job,
        AudioProcessingStep step,
        CancellationToken cancellationToken)
    {
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var stored = await context.SongUploadJobs.FirstOrDefaultAsync(
                j => j.MediaGuid == job.MediaGuid,
                cancellationToken);
            if (stored is not null && AudioProcessingProgressCalculator.IsAdvance(stored.Step, step))
            {
                stored.Step = step;
                stored.Status = SongUploadJobStatus.Processing;
                stored.StepUpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        job.Step = step;
        await _progressNotifier.NotifyStepAsync(job.CreatorId, job.MediaGuid, step, cancellationToken: cancellationToken);
    }

    private async Task MarkCompletedAsync(
        SongUploadJob job,
        int songMetadataId,
        AudioTranscodeResult result,
        CancellationToken cancellationToken)
    {
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var stored = await context.SongUploadJobs.FirstOrDefaultAsync(
                j => j.MediaGuid == job.MediaGuid,
                cancellationToken);
            if (stored is not null)
            {
                stored.Status = SongUploadJobStatus.Completed;
                stored.Step = AudioProcessingStep.Completed;
                stored.StepUpdatedAt = DateTime.UtcNow;
                stored.CompletedAt = DateTime.UtcNow;
                stored.SongMetadataId = songMetadataId;
                stored.DurationSeconds = result.DurationSeconds;
                stored.SourceWasAlreadyMp3 = result.SourceWasAlreadyMp3;
                stored.PlaybackBlobPath = result.PlaybackBlobPath;
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        await _progressNotifier.NotifyStepAsync(
            job.CreatorId,
            job.MediaGuid,
            AudioProcessingStep.Completed,
            job.SongTitle,
            cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];
}
