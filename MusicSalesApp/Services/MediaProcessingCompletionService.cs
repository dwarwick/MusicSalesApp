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
    private static readonly TimeSpan CopyPollInterval = TimeSpan.FromSeconds(2);

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IBlobContainerFactory _containerFactory;
    private readonly ISongMetadataService _metadataService;
    private readonly IOpenGraphService _openGraphService;
    private readonly IImageVariantCoordinator _imageVariantCoordinator;
    private readonly ISongUploadJobService _jobService;
    private readonly IUploadProgressNotifier _progressNotifier;
    private readonly IOptions<MediaProcessingOptions> _options;
    private readonly ILogger<MediaProcessingCompletionService> _logger;

    public MediaProcessingCompletionService(
        IDbContextFactory<AppDbContext> contextFactory,
        IBlobContainerFactory containerFactory,
        ISongMetadataService metadataService,
        IOpenGraphService openGraphService,
        IImageVariantCoordinator imageVariantCoordinator,
        ISongUploadJobService jobService,
        IUploadProgressNotifier progressNotifier,
        IOptions<MediaProcessingOptions> options,
        ILogger<MediaProcessingCompletionService> logger)
    {
        _contextFactory = contextFactory;
        _containerFactory = containerFactory;
        _metadataService = metadataService;
        _openGraphService = openGraphService;
        _imageVariantCoordinator = imageVariantCoordinator;
        _jobService = jobService;
        _progressNotifier = progressNotifier;
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

        if (job.Status == SongUploadJobStatus.Completed)
        {
            // The retry path, not an error. Say nothing and return 200.
            _logger.LogInformation("Completion callback replayed for already-completed job {JobId}", result.JobId);
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
            var sasQuery = CreateStagingReadSasQuery(staging);

            var playbackSource = string.IsNullOrWhiteSpace(result.PlaybackBlobPath)
                ? MediaProcessingStagingPaths.Playback(mediaGuid)
                : result.PlaybackBlobPath;

            if (result.SourceWasAlreadyMp3 || string.Equals(originalPath, mp3Path, StringComparison.OrdinalIgnoreCase))
            {
                // SongMediaPaths.OriginalAudio collapses onto the playback path for an MP3 source,
                // so the upload is both the original and the playback copy and is stored once.
                await CopyAcrossAccountsAsync(
                    staging, job.SourceBlobPath, media, mp3Path, sasQuery, assemblyToken, cancellationToken);
                copied.Add(mp3Path);
            }
            else
            {
                await CopyAcrossAccountsAsync(
                    staging, job.SourceBlobPath, media, originalPath, sasQuery, assemblyToken, cancellationToken);
                copied.Add(originalPath);

                await CopyAcrossAccountsAsync(
                    staging, playbackSource, media, mp3Path, sasQuery, assemblyToken, cancellationToken);
                copied.Add(mp3Path);
            }

            if (imagePath is not null && originalImagePath is not null && !string.IsNullOrWhiteSpace(job.CoverArtBlobPath))
            {
                await CopyAcrossAccountsAsync(
                    staging, job.CoverArtBlobPath, media, imagePath, sasQuery, assemblyToken, cancellationToken);
                copied.Add(imagePath);

                // The served copy and the retained original start identical; they diverge the first
                // time the art is cropped, which only rewrites the served copy.
                await CopyAcrossAccountsAsync(
                    staging, job.CoverArtBlobPath, media, originalImagePath, sasQuery, assemblyToken, cancellationToken);
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
                OriginalCoverArtFileName = job.CoverArtFileName,
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

                // Swallows its own failures. A song with no recorded rendition widths serves its
                // full-size master, exactly as it did before renditions existed - so an image
                // hiccup must not stop this job reaching Completed.
                await _imageVariantCoordinator.RefreshCoverArtVariantsAsync(
                    savedMetadata.Id,
                    cancellationToken: assemblyToken);
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
    }

    private string CreateStagingReadSasQuery(BlobContainerClient staging)
    {
        // Needs a shared-key credential. The app authenticates both accounts with account-key
        // connection strings, so this holds; the guard makes the failure legible if that changes.
        if (!staging.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                $"Cannot generate a SAS for staging container '{staging.Name}'. "
                + "Cross-account assembly requires a key-based connection string.");
        }

        var lifetime = _options.Value.StagingSasLifetime;
        var sasUri = staging.GenerateSasUri(
            BlobContainerSasPermissions.Read,
            DateTimeOffset.UtcNow.Add(lifetime));

        return sasUri.Query.TrimStart('?');
    }

    /// <param name="assemblyToken">
    /// The whole assembly's budget, shared by every copy. Each copy used to carry its own generous
    /// timeout, which bounded them individually but not together - five copies could each sit just
    /// inside their own limit and still outlast the caller waiting on all of them.
    /// </param>
    /// <param name="requestAborted">
    /// The original request token, used only to tell "the budget ran out" apart from "the Function
    /// hung up", so the log says which one happened.
    /// </param>
    private static async Task CopyAcrossAccountsAsync(
        BlobContainerClient sourceContainer,
        string sourcePath,
        BlobContainerClient destinationContainer,
        string destinationPath,
        string sourceSasQuery,
        CancellationToken assemblyToken,
        CancellationToken requestAborted)
    {
        var sourceBlob = sourceContainer.GetBlobClient(sourcePath);
        var destinationBlob = destinationContainer.GetBlobClient(destinationPath);
        var sourceUri = new UriBuilder(sourceBlob.Uri) { Query = sourceSasQuery }.Uri;

        var operation = await destinationBlob.StartCopyFromUriAsync(
            sourceUri,
            new BlobCopyFromUriOptions(),
            assemblyToken);

        // A same-account copy usually reports completion immediately; a cross-account one moves
        // real bytes, so expect to poll here.
        if (operation.HasCompleted)
        {
            return;
        }

        try
        {
            await operation.WaitForCompletionAsync(CopyPollInterval, assemblyToken);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Copying '{sourcePath}' into '{destinationContainer.Name}/{destinationPath}' "
                + $"did not finish inside the {MediaProcessingTimeouts.Assembly.TotalMinutes:0}-minute "
                + "assembly budget.");
        }
    }

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
