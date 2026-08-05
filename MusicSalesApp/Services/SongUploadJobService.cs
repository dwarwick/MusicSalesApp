#nullable enable
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>What the caller supplies to start one song's processing.</summary>
public sealed class SongUploadJobRequest
{
    public required Stream AudioStream { get; init; }
    public required string AudioFileName { get; init; }
    public required string SongTitle { get; init; }
    public required int CreatorId { get; init; }
    public string? AlbumName { get; init; }
    public Stream? CoverArtStream { get; init; }
    public string? CoverArtFileName { get; init; }

    /// <summary>
    /// Reports bytes staged so far, so the upload page can fill the first 15% of the progress bar
    /// while the browser stream is still being written to Azure.
    /// </summary>
    public IProgress<double>? StagingProgress { get; init; }
}

/// <summary>
/// Stages a creator's upload and hands it to the Azure Function for FFmpeg processing.
/// </summary>
public interface ISongUploadJobService
{
    /// <summary>
    /// Validates, stages to blob storage, records a <see cref="SongUploadJob"/> and enqueues the
    /// transcode. Returns as soon as the message is queued; the song does not exist yet.
    /// </summary>
    Task<SongUploadJob> CreateAsync(SongUploadJobRequest request, CancellationToken cancellationToken = default);

    /// <summary>The creator's jobs that have not reached a terminal state, oldest first.</summary>
    Task<List<SongUploadJob>> GetActiveJobsAsync(int creatorId, CancellationToken cancellationToken = default);

    /// <summary>Recent jobs for a creator, including finished ones, newest first.</summary>
    Task<List<SongUploadJob>> GetRecentJobsAsync(int creatorId, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>Deletes a job's staged blobs. Safe to call twice.</summary>
    Task DeleteStagedBlobsAsync(SongUploadJob job, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SongUploadJobService : ISongUploadJobService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IBlobContainerFactory _containerFactory;
    private readonly IMediaProcessingQueueClient _queueClient;
    private readonly ISongMetadataService _metadataService;
    private readonly IMusicService _musicService;
    private readonly ILogger<SongUploadJobService> _logger;

    public SongUploadJobService(
        IDbContextFactory<AppDbContext> contextFactory,
        IBlobContainerFactory containerFactory,
        IMediaProcessingQueueClient queueClient,
        ISongMetadataService metadataService,
        IMusicService musicService,
        ILogger<SongUploadJobService> logger)
    {
        _contextFactory = contextFactory;
        _containerFactory = containerFactory;
        _queueClient = queueClient;
        _metadataService = metadataService;
        _musicService = musicService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SongUploadJob> CreateAsync(
        SongUploadJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.AudioStream);

        if (!MusicFileExtensions.IsAudioFile(request.AudioFileName))
        {
            throw new InvalidDataException($"'{request.AudioFileName}' does not have a supported audio extension.");
        }

        // Cover art is optional, but a stream without a filename is a caller bug rather than a bad
        // upload - the extension is what decides the stored blob's name and content type. Captured
        // into a local so the rest of the method can treat the pair as either both present or both
        // absent.
        string? coverArtFileName = null;
        if (request.CoverArtStream is not null)
        {
            if (string.IsNullOrWhiteSpace(request.CoverArtFileName)
                || !MusicFileExtensions.IsCoverArtFile(request.CoverArtFileName))
            {
                throw new InvalidDataException(
                    $"'{request.CoverArtFileName}' does not have a supported cover-art extension.");
            }

            coverArtFileName = request.CoverArtFileName;
        }

        var songTitle = (request.SongTitle ?? string.Empty).Trim();
        var titleErrors = SongTitleHelper.GetTitleValidationErrors(songTitle);
        if (titleErrors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", titleErrors));
        }

        var audioExtension = Path.GetExtension(request.AudioFileName).ToLowerInvariant();
        var coverExtension = coverArtFileName is null
            ? null
            : Path.GetExtension(coverArtFileName).ToLowerInvariant();

        // A freshly minted GUID, exactly as MusicUploadService always did - it cannot collide with
        // an existing song, so no per-path upload lock is needed. What is new is that the same GUID
        // now also names the staging folder and correlates every queue message and callback.
        var mediaGuid = Guid.NewGuid();

        // Run the ownership/collision check synchronously so the creator still learns about a
        // conflict in the review step, rather than minutes later via a failed job.
        await _metadataService.ValidateUploadTargetAsync(
            SongMediaPaths.Playback(mediaGuid),
            SongMediaPaths.OriginalAudio(mediaGuid, audioExtension),
            coverExtension is null ? null : SongMediaPaths.CoverArt(mediaGuid, coverExtension),
            request.CreatorId);

        // Cheap container sniff before anything reaches Azure. This is the header check only - no
        // FFmpeg - so it stays fast enough to run on the request thread. The real decode happens in
        // the Function.
        request.AudioStream.Position = 0;
        if (!await _musicService.IsValidAudioFileAsync(request.AudioStream, request.AudioFileName))
        {
            throw new InvalidDataException(
                $"'{request.AudioFileName}' does not contain audio matching its extension.");
        }

        if (request.CoverArtStream is not null && coverArtFileName is not null)
        {
            request.CoverArtStream.Position = 0;
            if (!MediaFileContentValidator.ImageContentMatchesExtension(
                    request.CoverArtStream,
                    coverArtFileName,
                    out _))
            {
                throw new InvalidDataException(
                    $"'{coverArtFileName}' does not contain a decodable image matching its extension.");
            }
        }

        // Resolved only once the creator's own input has passed every check, so a bad file reports
        // as a bad file rather than as a configuration error.
        var staging = _containerFactory.GetUploadStagingContainer()
            ?? throw new InvalidOperationException(
                "Upload staging is not configured; AzureLowSpeed:UploadStagingContainerName or its connection string is missing.");

        await staging.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var sourcePath = MediaProcessingStagingPaths.Source(mediaGuid, audioExtension);
        var coverPath = coverExtension is null
            ? null
            : MediaProcessingStagingPaths.Cover(mediaGuid, coverExtension);

        var audioContentType = MusicFileExtensions.GetAudioContentType(audioExtension)
            ?? throw new InvalidDataException("Unsupported original audio type.");

        request.AudioStream.Position = 0;
        var audioLength = await UploadStagedAsync(
            staging,
            sourcePath,
            request.AudioStream,
            audioContentType,
            request.StagingProgress,
            cancellationToken);

        if (coverPath is not null && request.CoverArtStream is not null)
        {
            var coverContentType = MusicFileExtensions.GetCoverArtContentType(coverExtension!)
                ?? throw new InvalidDataException("Unsupported cover-art type.");
            request.CoverArtStream.Position = 0;
            await UploadStagedAsync(
                staging,
                coverPath,
                request.CoverArtStream,
                coverContentType,
                progress: null,
                cancellationToken);
        }

        var job = new SongUploadJob
        {
            MediaGuid = mediaGuid,
            CreatorId = request.CreatorId,
            SongTitle = songTitle,
            AlbumName = request.AlbumName ?? string.Empty,
            SourceBlobPath = sourcePath,
            SourceFileName = Path.GetFileName(request.AudioFileName),
            SourceExtension = audioExtension,
            SourceFileSize = audioLength,
            SourceContentType = audioContentType,
            CoverArtBlobPath = coverPath,
            CoverArtFileName = coverArtFileName is null
                ? null
                : Path.GetFileName(coverArtFileName),
            CoverArtExtension = coverExtension,
            Status = SongUploadJobStatus.Queued,
            Step = AudioProcessingStep.Queued,
            StepUpdatedAt = DateTime.UtcNow
        };

        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            context.SongUploadJobs.Add(job);
            await context.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await _queueClient.EnqueueTranscodeAsync(
                new AudioTranscodeRequest
                {
                    JobId = mediaGuid,
                    SourceBlobPath = sourcePath,
                    SourceFileName = job.SourceFileName,
                    SourceExtension = audioExtension,
                    PlaybackBlobPath = MediaProcessingStagingPaths.Playback(mediaGuid)
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The row exists but nothing will ever pick it up, so fail it here rather than leaving
            // the creator watching a bar that can never move. The reconciler would eventually catch
            // this, but minutes later and with a far less useful message.
            _logger.LogError(ex, "Could not enqueue transcode for job {JobId}", mediaGuid);
            await MarkEnqueueFailedAsync(mediaGuid, ex, cancellationToken);
            throw;
        }

        return job;
    }

    /// <inheritdoc />
    public async Task<List<SongUploadJob>> GetActiveJobsAsync(
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.SongUploadJobs
            .AsNoTracking()
            .Where(job => job.CreatorId == creatorId
                && (job.Status == SongUploadJobStatus.Queued || job.Status == SongUploadJobStatus.Processing))
            .OrderBy(job => job.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<SongUploadJob>> GetRecentJobsAsync(
        int creatorId,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.SongUploadJobs
            .AsNoTracking()
            .Where(job => job.CreatorId == creatorId)
            .OrderByDescending(job => job.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteStagedBlobsAsync(SongUploadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var staging = _containerFactory.GetUploadStagingContainer();
        if (staging is null)
        {
            return;
        }

        // Deleted by folder prefix rather than from the job's three path columns, because those
        // columns are not reliably populated at the moment cleanup runs. PlaybackBlobPath in
        // particular is written by the Function's callback, so a caller holding a snapshot read
        // before that - or a job that failed after the Function uploaded its MP3 - would skip the
        // transcode and leak it. The folder is named for the job's GUID and holds nothing else.
        var prefix = $"{MediaProcessingStagingPaths.Folder(job.MediaGuid)}/";

        try
        {
            await foreach (var blob in staging.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
            {
                try
                {
                    await staging.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    // Staging has a lifecycle rule that deletes anything left behind, so a failure
                    // here costs a few days of storage rather than correctness.
                    _logger.LogWarning(ex, "Could not delete staged blob {Path} for job {JobId}", blob.Name, job.MediaGuid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate staged blobs for job {JobId}", job.MediaGuid);
        }
    }

    private static async Task<long> UploadStagedAsync(
        Azure.Storage.Blobs.BlobContainerClient staging,
        string path,
        Stream content,
        string contentType,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var blob = staging.GetBlobClient(path);
        var length = content.CanSeek ? content.Length : 0L;

        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                ProgressHandler = progress is null || length <= 0
                    ? null
                    : new Progress<long>(sent => progress.Report(Math.Clamp(sent * 100d / length, 0d, 100d)))
            },
            cancellationToken);

        return length > 0 ? length : (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value.ContentLength;
    }

    private async Task MarkEnqueueFailedAsync(Guid mediaGuid, Exception ex, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var stored = await context.SongUploadJobs.FirstOrDefaultAsync(
                job => job.MediaGuid == mediaGuid,
                cancellationToken);
            if (stored is null)
            {
                return;
            }

            stored.Status = SongUploadJobStatus.Failed;
            stored.Step = AudioProcessingStep.Failed;
            stored.StepUpdatedAt = DateTime.UtcNow;
            stored.CompletedAt = DateTime.UtcNow;
            stored.FailureCode = MediaProcessingFailureCodes.Abandoned;
            stored.FailureMessage = "The processing request could not be queued. Please try uploading again.";
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception markEx)
        {
            _logger.LogError(markEx, "Could not mark job {JobId} failed after an enqueue error", mediaGuid);
        }
    }
}
