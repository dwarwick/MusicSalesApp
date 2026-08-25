#nullable enable
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>What the caller supplies to start one song's processing.</summary>
/// <summary>
/// What a creator sets about a song on the upload page, carried from the review step through
/// staging and transcoding to the published record.
///
/// <para>
/// It travels on the job rather than being applied afterwards because there is no "afterwards"
/// the page can reach: the song does not exist until the Function has transcoded the audio and
/// the API has assembled it, minutes later and on a different machine. Genre is the reason this
/// exists at all - it is required, and until now it could only be set on /creator/songs, one
/// dialog at a time, after the song was already published.
/// </para>
/// </summary>
public sealed record SongPublishMetadata
{
    /// <summary>The genre name, matching <see cref="SongMetadata.Genre"/>, which is a string
    /// rather than a foreign key.</summary>
    public string? Genre { get; init; }

    /// <summary>The persona whose name listeners see, or null to fall back to the artist name
    /// and then the creator display name. Null is a normal case: most creators have none.</summary>
    public int? PersonaId { get; init; }

    public bool IsAiGenerated { get; init; }
    public bool IsAiVocals { get; init; }
    public bool IsAiLyrics { get; init; }

    /// <summary>Nothing set. What every caller outside the upload page passes.</summary>
    public static SongPublishMetadata None { get; } = new();
}

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

    public SongPublishMetadata Metadata { get; init; } = SongPublishMetadata.None;
}

/// <summary>
/// What the caller supplies when the browser has already uploaded the bytes.
///
/// <para>
/// No streams: the web server never sees this song's audio. The GUID is minted before the upload
/// starts, because a destination path is needed before there is anything to record it against.
/// </para>
/// </summary>
public sealed class StagedSongUploadRequest
{
    /// <summary>Minted server-side when the write token was issued; names the staging folder.</summary>
    public required Guid MediaGuid { get; init; }

    public required string AudioFileName { get; init; }
    public required string SongTitle { get; init; }
    public required int CreatorId { get; init; }
    public string? AlbumName { get; init; }

    public SongPublishMetadata Metadata { get; init; } = SongPublishMetadata.None;

    public string? CoverArtFileName { get; init; }

    /// <summary>
    /// Where the matched cover image currently sits, under <c>batch/{batchId}/</c>.
    ///
    /// <para>
    /// It cannot already be in the song's folder: which image belongs to which song is only known
    /// after matching, which is after the images were uploaded. This service copies it across, which
    /// is a same-account server-side operation and moves no bytes through the web host.
    /// </para>
    /// </summary>
    public string? CoverArtStagedPath { get; init; }
}

/// <summary>
/// Stages a creator's upload and hands it to the Azure Function for FFmpeg processing.
/// </summary>
public interface ISongUploadJobService
{
    /// <summary>
    /// Records and enqueues a song whose audio the browser has already uploaded to staging.
    ///
    /// <para>
    /// The same job as <see cref="CreateAsync"/> minus the transfer. Every check that only needs
    /// metadata runs identically; the two that needed the bytes are done against the staged blob
    /// instead - its length for the size cap, and a 64-byte ranged read for the container sniff.
    /// </para>
    /// </summary>
    Task<SongUploadJob> CreateFromStagedAsync(
        StagedSongUploadRequest request,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Deletes a staging folder by media GUID, for a job whose row was never written.
    ///
    /// <para>
    /// The row-based overload is the normal path, but a GUID can be minted and its folder written to
    /// before <see cref="CreateAsync"/> commits anything - a validation failure after staging leaves
    /// blobs that no <see cref="SongUploadJob"/> points at. Fabricating a throwaway row to reach the
    /// other overload would be less honest than saying what this actually needs, which is the GUID.
    /// </para>
    /// </summary>
    Task DeleteStagedBlobsAsync(Guid mediaGuid, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SongUploadJobService : ISongUploadJobService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IBlobContainerFactory _containerFactory;
    private readonly IMediaProcessingQueueClient _queueClient;
    private readonly ISongMetadataService _metadataService;
    private readonly IMusicService _musicService;
    private readonly IAppSettingsService _appSettings;
    private readonly IStagedBlobReader _stagedBlobs;
    private readonly ILogger<SongUploadJobService> _logger;

    public SongUploadJobService(
        IDbContextFactory<AppDbContext> contextFactory,
        IBlobContainerFactory containerFactory,
        IMediaProcessingQueueClient queueClient,
        ISongMetadataService metadataService,
        IMusicService musicService,
        IAppSettingsService appSettings,
        IStagedBlobReader stagedBlobs,
        ILogger<SongUploadJobService> logger)
    {
        _contextFactory = contextFactory;
        _containerFactory = containerFactory;
        _queueClient = queueClient;
        _metadataService = metadataService;
        _musicService = musicService;
        _appSettings = appSettings;
        _stagedBlobs = stagedBlobs;
        _logger = logger;
    }

    private const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>
    /// Rejects a stream larger than the admin cap, before it is staged.
    /// </summary>
    /// <remarks>
    /// A non-seekable stream has no length to check and is let through rather than refused: every
    /// caller today supplies a buffered file, and failing an upload because a length was unavailable
    /// would trade a real upload for a hypothetical one. The Function still bounds what it will
    /// process, and the staging lifecycle rule bounds what an oversized file can cost.
    /// </remarks>
    private static Task EnsureWithinSizeLimitAsync(Stream stream, string fileName, int maxSizeMB)
    {
        if (!stream.CanSeek || maxSizeMB <= 0)
        {
            return Task.CompletedTask;
        }

        var maxBytes = maxSizeMB * BytesPerMegabyte;
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException(
                $"'{fileName}' is {stream.Length / (double)BytesPerMegabyte:F1} MB, "
                + $"which is larger than the {maxSizeMB} MB limit.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<SongUploadJob> CreateFromStagedAsync(
        StagedSongUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.MediaGuid == Guid.Empty)
        {
            throw new InvalidDataException("A media GUID is required for a staged upload.");
        }

        if (!MusicFileExtensions.IsAudioFile(request.AudioFileName))
        {
            throw new InvalidDataException($"'{request.AudioFileName}' does not have a supported audio extension.");
        }

        string? coverArtFileName = null;
        if (!string.IsNullOrWhiteSpace(request.CoverArtStagedPath))
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
        var mediaGuid = request.MediaGuid;

        // Runs here as well as when the token was minted. Minutes pass between those two moments now,
        // and an admin art-replace or a second tab could claim these paths in between - dropping it
        // here would turn an ownership check into a time-of-check/time-of-use gap.
        await _metadataService.ValidateUploadTargetAsync(
            SongMediaPaths.Playback(mediaGuid),
            SongMediaPaths.OriginalAudio(mediaGuid, audioExtension),
            coverExtension is null ? null : SongMediaPaths.CoverArt(mediaGuid, coverExtension),
            request.CreatorId);

        var sourcePath = MediaProcessingStagingPaths.Source(mediaGuid, audioExtension);

        // Existence and size in one call. The browser enforced the cap too, but that is the creator's
        // own machine telling us how big their file is - this is the first time anything we control
        // has measured it, and it is the only remaining enforcement now that OpenReadStream is gone.
        var audioLength = await _stagedBlobs.GetLengthAsync(sourcePath, cancellationToken);
        if (audioLength is null || audioLength == 0)
        {
            // Zero is treated as absent rather than falling through to the header sniff. A committed
            // but empty blob is what an interrupted single-PUT leaves behind, and a ranged read of
            // its first 64 bytes is answered with 416, not with an empty body - so the sniff below
            // would fault instead of rejecting it, and the creator would get a raw Azure message.
            await DeleteStagedBlobsAsync(mediaGuid, CancellationToken.None);
            throw new InvalidDataException(
                $"'{request.AudioFileName}' was never fully uploaded. Please try adding it again.");
        }

        var maxAudioMB = await _appSettings.GetMaxAudioUploadSizeMBAsync();
        if (maxAudioMB > 0 && audioLength > maxAudioMB * BytesPerMegabyte)
        {
            // Deleted rather than left: the row is never written, so nothing else will ever know this
            // blob exists, and the lifecycle rule would sit on it for a week.
            await DeleteStagedBlobsAsync(mediaGuid, CancellationToken.None);
            throw new InvalidDataException(
                $"'{request.AudioFileName}' is {audioLength.Value / (double)BytesPerMegabyte:F1} MB, "
                + $"which is larger than the {maxAudioMB} MB limit.");
        }

        // 64 bytes, not 60 MB. Same header sniff the streamed path does, against the staged blob.
        var header = await _stagedBlobs.ReadHeaderAsync(sourcePath, AudioHeaderByteCount, cancellationToken);
        if (header is null || !IsAudioHeaderValid(header, request.AudioFileName))
        {
            await DeleteStagedBlobsAsync(mediaGuid, CancellationToken.None);
            throw new InvalidDataException(
                $"'{request.AudioFileName}' does not contain audio matching its extension.");
        }

        string? coverPath = null;
        if (coverExtension is not null && request.CoverArtStagedPath is not null)
        {
            // The image gets the same treatment the audio just had, and for the same reason. The
            // streamed path enforces this cap on the cover-art stream; without it here the direct
            // path had no server-side image limit at all - the write token minted for a batch image
            // is Create|Write with no size bound, so anything the browser was willing to PUT would
            // be copied in and handed to the Function.
            var coverLength = await _stagedBlobs.GetLengthAsync(request.CoverArtStagedPath, cancellationToken);
            if (coverLength is null)
            {
                throw new InvalidDataException(
                    $"'{coverArtFileName}' was never fully uploaded. Please try adding it again.");
            }

            var maxImageMB = await _appSettings.GetMaxImageUploadSizeMBAsync();
            if (maxImageMB > 0 && coverLength > maxImageMB * BytesPerMegabyte)
            {
                // The song is not failed over its artwork. The audio is already staged and valid,
                // and refusing the whole upload because a cover is too large would cost the creator
                // the expensive half of the transfer for the cheap half's mistake.
                _logger.LogWarning(
                    "Cover art '{CoverArtFileName}' is {Size:F1} MB, over the {Max} MB limit. "
                    + "Publishing {AudioFileName} without artwork.",
                    coverArtFileName,
                    coverLength.Value / (double)BytesPerMegabyte,
                    maxImageMB,
                    request.AudioFileName);

                coverArtFileName = null;
                coverExtension = null;
            }
            else
            {
                coverPath = MediaProcessingStagingPaths.Cover(mediaGuid, coverExtension);
                await _stagedBlobs.CopyWithinStagingAsync(request.CoverArtStagedPath, coverPath, cancellationToken);
            }
        }

        // No decode. That check moved to the Function, which already decodes this image to build its
        // renditions and now skips the cover-art copy entirely when it cannot - so an undecodable
        // image costs the song its artwork rather than the catalogue an unrenderable master.
        var audioContentType = MusicFileExtensions.GetAudioContentType(audioExtension)
            ?? throw new InvalidDataException("Unsupported original audio type.");

        return await PersistAndEnqueueAsync(
            mediaGuid,
            request.CreatorId,
            songTitle,
            request.AlbumName,
            sourcePath,
            Path.GetFileName(request.AudioFileName),
            audioExtension,
            audioLength.Value,
            audioContentType,
            coverPath,
            coverArtFileName is null ? null : Path.GetFileName(coverArtFileName),
            coverExtension,
            request.Metadata,
            cancellationToken);
    }

    /// <summary>Bytes the container sniff needs. Matches what AudioContainerSniffer reads.</summary>
    private const int AudioHeaderByteCount = 64;

    private bool IsAudioHeaderValid(byte[] header, string fileName)
    {
        using var buffer = new MemoryStream(header);
        return AudioContainerSniffer.ContentMatchesExtension(buffer, fileName, out _);
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

        // Size gate. Until now the admin caps were enforced in exactly one place - the upload page's
        // IBrowserFile.OpenReadStream(maxAllowedSize) - and nowhere on this side at all. There is no
        // Kestrel or IIS body limit either, so any caller reaching this method directly could stage a
        // file of any size. Checking here makes the cap a property of the pipeline rather than of one
        // page, which matters increasingly as callers stop being that page.
        await EnsureWithinSizeLimitAsync(
            request.AudioStream,
            request.AudioFileName,
            await _appSettings.GetMaxAudioUploadSizeMBAsync());

        if (request.CoverArtStream is not null && coverArtFileName is not null)
        {
            await EnsureWithinSizeLimitAsync(
                request.CoverArtStream,
                coverArtFileName,
                await _appSettings.GetMaxImageUploadSizeMBAsync());
        }

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

        return await PersistAndEnqueueAsync(
            mediaGuid,
            request.CreatorId,
            songTitle,
            request.AlbumName,
            sourcePath,
            Path.GetFileName(request.AudioFileName),
            audioExtension,
            audioLength,
            audioContentType,
            coverPath,
            coverArtFileName is null ? null : Path.GetFileName(coverArtFileName),
            coverExtension,
            request.Metadata,
            cancellationToken);
    }

    /// <summary>
    /// Writes the job row and queues the transcode.
    ///
    /// <para>
    /// Shared by both entry points deliberately. How the bytes got to staging differs; what a job
    /// looks like and what the Function is told about it must not, or a song uploaded one way would
    /// process differently from the same song uploaded the other.
    /// </para>
    /// </summary>
    private async Task<SongUploadJob> PersistAndEnqueueAsync(
        Guid mediaGuid,
        int creatorId,
        string songTitle,
        string? albumName,
        string sourcePath,
        string sourceFileName,
        string audioExtension,
        long audioLength,
        string audioContentType,
        string? coverPath,
        string? coverArtFileName,
        string? coverExtension,
        SongPublishMetadata metadata,
        CancellationToken cancellationToken)
    {
        var job = new SongUploadJob
        {
            MediaGuid = mediaGuid,
            CreatorId = creatorId,
            SongTitle = songTitle,
            AlbumName = albumName ?? string.Empty,

            // Carried, not applied: the song row does not exist yet. The assembly step copies
            // these onto it once the Function has finished with the audio.
            Genre = metadata.Genre,
            PersonaId = metadata.PersonaId,
            IsAiGenerated = metadata.IsAiGenerated,
            IsAiVocals = metadata.IsAiVocals,
            IsAiLyrics = metadata.IsAiLyrics,
            SourceBlobPath = sourcePath,
            SourceFileName = sourceFileName,
            SourceExtension = audioExtension,
            SourceFileSize = audioLength,
            SourceContentType = audioContentType,
            CoverArtBlobPath = coverPath,
            CoverArtFileName = coverArtFileName,
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
                    PlaybackBlobPath = MediaProcessingStagingPaths.Playback(mediaGuid),

                    // Both null for an audio-only upload, which is how the Function knows to skip
                    // the image work entirely. It derives the rendition paths from the job GUID plus
                    // this extension, through the same SongMediaPaths helper the assembly step uses.
                    CoverArtBlobPath = coverPath,
                    CoverArtExtension = coverExtension
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
    public Task DeleteStagedBlobsAsync(SongUploadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Deleted by folder prefix rather than from the job's three path columns, because those
        // columns are not reliably populated at the moment cleanup runs. PlaybackBlobPath in
        // particular is written by the Function's callback, so a caller holding a snapshot read
        // before that - or a job that failed after the Function uploaded its MP3 - would skip the
        // transcode and leak it. The folder is named for the job's GUID and holds nothing else.
        //
        // Which means the GUID is the whole input, and the row is only where this one reads it from.
        return DeleteStagedBlobsAsync(job.MediaGuid, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteStagedBlobsAsync(Guid mediaGuid, CancellationToken cancellationToken = default)
    {
        if (mediaGuid == Guid.Empty)
        {
            return;
        }

        var staging = _containerFactory.GetUploadStagingContainer();
        if (staging is null)
        {
            return;
        }

        var prefix = $"{MediaProcessingStagingPaths.Folder(mediaGuid)}/";

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
                    _logger.LogWarning(ex, "Could not delete staged blob {Path} for job {JobId}", blob.Name, mediaGuid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate staged blobs for job {JobId}", mediaGuid);
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
