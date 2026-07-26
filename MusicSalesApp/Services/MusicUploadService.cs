using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class MusicUploadService : IMusicUploadService
{
    private readonly IAzureStorageService _storageService;
    private readonly IMusicService _musicService;
    private readonly ISongMetadataService _metadataService;
    private readonly IOpenGraphService _openGraphService;
    private readonly ILogger<MusicUploadService> _logger;

    public MusicUploadService(
        IAzureStorageService storageService,
        IMusicService musicService,
        ISongMetadataService metadataService,
        IOpenGraphService openGraphService,
        ILogger<MusicUploadService> logger)
    {
        _storageService = storageService;
        _musicService = musicService;
        _metadataService = metadataService;
        _openGraphService = openGraphService;
        _logger = logger;
    }

    public async Task<string> UploadAudioAsync(
        IFormFile file,
        string destinationFolder,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length <= 0)
        {
            throw new ArgumentException("No file uploaded.", nameof(file));
        }

        await using var stream = file.OpenReadStream();
        return await UploadAudioAsync(stream, file.FileName, destinationFolder, cancellationToken);
    }

    public async Task<string> UploadAudioAsync(
        Stream fileStream,
        string originalFileName,
        string destinationFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        RequireSupportedAudioExtension(originalFileName);

        await using var buffered = await BufferUploadFileAsync(fileStream, cancellationToken);
        BufferedUploadFile convertedPlayback = null;
        try
        {
            await using (var validationStream = buffered.OpenRead())
            {
                await ValidateAudioContentAsync(validationStream, originalFileName);
            }

            var playbackFile = buffered;
            if (!_musicService.IsMp3File(originalFileName))
            {
                await using var conversionInput = buffered.OpenRead();
                await using var convertedStream = await _musicService.ConvertToMp3Async(
                    conversionInput,
                    originalFileName);
                convertedPlayback = await BufferUploadFileAsync(convertedStream, cancellationToken);
                playbackFile = convertedPlayback;
            }

            var mp3Name = Path.ChangeExtension(Path.GetFileName(originalFileName), ".mp3");
            double duration;
            await using (var durationStream = playbackFile.OpenRead())
            {
                duration = await RequirePositiveDurationAsync(
                    durationStream,
                    mp3Name,
                    cancellationToken);
            }

            var path = string.IsNullOrWhiteSpace(destinationFolder)
                ? mp3Name
                : $"{destinationFolder.TrimEnd('/')}/{mp3Name}";

            await _storageService.EnsureContainerExistsAsync();
            await using (var uploadStream = playbackFile.OpenRead())
            {
                await _storageService.UploadAsync(path, uploadStream, "audio/mpeg");
            }

            _logger.LogInformation("Uploaded validated audio {Path} with duration {Duration}", path, duration);
            return path;
        }
        finally
        {
            if (convertedPlayback != null)
            {
                await convertedPlayback.DisposeAsync();
            }
        }
    }

    public Task<MusicUploadResult> UploadMusicWithAlbumArtAsync(
        Stream audioStream,
        string audioFileName,
        Stream albumArtStream,
        string albumArtFileName,
        string songTitle,
        string albumName = null,
        int? creatorId = null,
        CancellationToken cancellationToken = default)
        => UploadMusicAsync(
            audioStream,
            audioFileName,
            albumArtStream,
            albumArtFileName,
            songTitle,
            albumName,
            creatorId,
            cancellationToken);

    public Task<MusicUploadResult> UploadMusicWithoutAlbumArtAsync(
        Stream audioStream,
        string audioFileName,
        string songTitle,
        string albumName = null,
        int? creatorId = null,
        CancellationToken cancellationToken = default)
        => UploadMusicAsync(
            audioStream,
            audioFileName,
            null,
            null,
            songTitle,
            albumName,
            creatorId,
            cancellationToken);

    private async Task<MusicUploadResult> UploadMusicAsync(
        Stream audioStream,
        string audioFileName,
        Stream albumArtStream,
        string albumArtFileName,
        string songTitle,
        string albumName,
        int? creatorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audioStream);
        if (creatorId == null)
        {
            throw new InvalidOperationException("Cannot upload music without a creator ID.");
        }

        RequireSupportedAudioExtension(audioFileName);
        if (albumArtStream != null)
        {
            RequireSupportedImageExtension(albumArtFileName);
        }

        // The creator's filename no longer determines anything about storage, so the title has to
        // be supplied explicitly rather than derived from it.
        songTitle = (songTitle ?? string.Empty).Trim();
        var titleErrors = SongTitleHelper.GetTitleValidationErrors(songTitle);
        if (titleErrors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", titleErrors));
        }

        await using var bufferedAudio = await BufferUploadFileAsync(audioStream, cancellationToken);
        var bufferedImage = albumArtStream == null
            ? null
            : await BufferUploadFileAsync(albumArtStream, cancellationToken);

        BufferedUploadFile convertedPlayback = null;
        var blobMutations = new List<BlobMutation>();
        var rollbackRequired = true;
        try
        {
            // All decoding and format checks happen before the first Azure operation.
            await using (var audioValidationStream = bufferedAudio.OpenRead())
            {
                await ValidateAudioContentAsync(audioValidationStream, audioFileName);
            }

            if (bufferedImage != null)
            {
                await using var imageValidationStream = bufferedImage.OpenRead();
                ValidateImageContent(imageValidationStream, albumArtFileName);
            }

            var originalExtension = Path.GetExtension(audioFileName).ToLowerInvariant();
            var imageExtension = albumArtFileName == null
                ? null
                : Path.GetExtension(albumArtFileName).ToLowerInvariant();
            // A freshly minted GUID cannot collide with an existing song, which is why there is no
            // longer a per-path upload lock: nothing else can be writing to this folder.
            var mediaGuid = Guid.NewGuid();
            var mp3Path = SongMediaPaths.Playback(mediaGuid);
            var originalPath = SongMediaPaths.OriginalAudio(mediaGuid, originalExtension);
            var imagePath = imageExtension == null
                ? null
                : SongMediaPaths.CoverArt(mediaGuid, imageExtension);
            var originalImagePath = imageExtension == null
                ? null
                : SongMediaPaths.OriginalCoverArt(mediaGuid, imageExtension);
            var previousMetadata = await _metadataService.ValidateUploadTargetAsync(
                mp3Path,
                originalPath,
                imagePath,
                creatorId.Value);
            var previousOriginalPath = previousMetadata?.OriginalAudioBlobPath;
            var previousImagePath = previousMetadata?.ImageBlobPath;

            var playbackFile = bufferedAudio;
            if (!_musicService.IsMp3File(audioFileName))
            {
                await using var conversionInput = bufferedAudio.OpenRead();
                await using var convertedStream = await _musicService.ConvertToMp3Async(
                    conversionInput,
                    audioFileName);
                convertedPlayback = await BufferUploadFileAsync(convertedStream, cancellationToken);
                playbackFile = convertedPlayback;

                await using var convertedValidationStream = playbackFile.OpenRead();
                if (!MediaFileContentValidator.AudioContentMatchesExtension(
                        convertedValidationStream,
                        ".mp3",
                        out _))
                {
                    throw new InvalidDataException("FFmpeg did not produce a valid MP3 container.");
                }
            }

            double duration;
            await using (var durationStream = playbackFile.OpenRead())
            {
                // Report the creator's own filename rather than the GUID blob name: this string
                // only feeds the extension check and the diagnostics the creator ends up reading.
                duration = await RequirePositiveDurationAsync(
                    durationStream,
                    Path.ChangeExtension(Path.GetFileName(audioFileName), ".mp3"),
                    cancellationToken);
            }

            var originalContentType = MusicFileExtensions.GetAudioContentType(originalExtension)
                ?? throw new InvalidDataException("Unsupported original audio type.");

            await _storageService.EnsureContainerExistsAsync();

            if (string.Equals(originalPath, mp3Path, StringComparison.OrdinalIgnoreCase))
            {
                await using var originalUploadStream = bufferedAudio.OpenRead();
                await UploadTrackedAsync(
                    mp3Path,
                    originalUploadStream,
                    "audio/mpeg",
                    blobMutations,
                    cancellationToken);
            }
            else
            {
                await using (var originalUploadStream = bufferedAudio.OpenRead())
                {
                    await UploadTrackedAsync(
                        originalPath,
                        originalUploadStream,
                        originalContentType,
                        blobMutations,
                        cancellationToken);
                }

                await using var playbackUploadStream = playbackFile.OpenRead();
                await UploadTrackedAsync(
                    mp3Path,
                    playbackUploadStream,
                    "audio/mpeg",
                    blobMutations,
                    cancellationToken);
            }

            if (imagePath != null)
            {
                var imageContentType = MusicFileExtensions.GetCoverArtContentType(imageExtension)
                    ?? throw new InvalidDataException("Unsupported cover-art type.");
                await using (var imageUploadStream = bufferedImage.OpenRead())
                {
                    await UploadTrackedAsync(
                        imagePath,
                        imageUploadStream,
                        imageContentType,
                        blobMutations,
                        cancellationToken);
                }

                // The served copy and the retained original start out identical; they diverge the
                // first time the cover art is cropped or replaced, which only rewrites the served copy.
                await using var originalImageUploadStream = bufferedImage.OpenRead();
                await UploadTrackedAsync(
                    originalImagePath,
                    originalImageUploadStream,
                    imageContentType,
                    blobMutations,
                    cancellationToken);
            }

            await _metadataService.UpsertValidatedUploadAsync(new SongMetadata
            {
                MediaGuid = mediaGuid,
                BlobPath = mp3Path,
                Mp3BlobPath = mp3Path,
                ImageBlobPath = imagePath,
                OriginalAudioBlobPath = originalPath,
                OriginalAudioFileName = Path.GetFileName(audioFileName),
                OriginalAudioFileSize = bufferedAudio.Length,
                OriginalAudioContentType = originalContentType,
                OriginalCoverArtBlobPath = originalImagePath,
                OriginalCoverArtFileName = albumArtFileName == null
                    ? null
                    : Path.GetFileName(albumArtFileName),
                FileExtension = ".mp3",
                SongTitle = songTitle,
                AlbumName = albumName ?? string.Empty,
                IsAlbumCover = false,
                TrackLength = duration,
                CreatorId = creatorId
            });

            // The metadata commit succeeded, so the newly uploaded blobs are now
            // live and referenced by the database. Nothing below this point should
            // trigger the catch block's restoration of blob snapshots.
            rollbackRequired = false;

            if (!string.IsNullOrWhiteSpace(previousOriginalPath)
                && !string.Equals(previousOriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _storageService.DeleteAsync(previousOriginalPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to delete previous original audio blob {Path}", previousOriginalPath);
                }
            }

            if (imagePath != null
                && !string.IsNullOrWhiteSpace(previousImagePath)
                && !string.Equals(previousImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _storageService.DeleteAsync(previousImagePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to delete previous cover art blob {Path}", previousImagePath);
                }
            }

            if (imagePath != null)
            {
                try
                {
                    // Nothing to invalidate: the GUID folder was created moments ago, so no
                    // sharing image can already exist for it.
                    await _openGraphService.PreGenerateFacebookImageAsync(imagePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to pre-generate sharing image for {Path}", imagePath);
                }
            }

            return new MusicUploadResult
            {
                MediaGuid = mediaGuid,
                SongTitle = songTitle,
                OriginalAudioBlobPath = originalPath,
                OriginalAudioFileName = Path.GetFileName(audioFileName),
                OriginalAudioFileSize = bufferedAudio.Length,
                OriginalAudioContentType = originalContentType,
                OriginalCoverArtBlobPath = originalImagePath,
                Mp3BlobPath = mp3Path,
                ImageBlobPath = imagePath,
                TrackDuration = duration
            };
        }
        catch
        {
            if (rollbackRequired)
            {
                foreach (var mutation in blobMutations.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (mutation.Existed)
                        {
                            await using var restoreStream = mutation.Backup!.OpenRead();
                            await _storageService.UploadAsync(
                                mutation.Path,
                                restoreStream,
                                mutation.PreviousContentType);
                        }
                        else
                        {
                            await _storageService.DeleteAsync(mutation.Path);
                        }
                    }
                    catch (Exception cleanupException)
                    {
                        _logger.LogCritical(
                            cleanupException,
                            "Failed to restore blob {Path} after upload rollback",
                            mutation.Path);
                    }
                }
            }

            throw;
        }
        finally
        {
            if (convertedPlayback != null)
            {
                await convertedPlayback.DisposeAsync();
            }

            if (bufferedImage != null)
            {
                await bufferedImage.DisposeAsync();
            }

            foreach (var mutation in blobMutations)
            {
                if (mutation.Backup != null)
                {
                    await mutation.Backup.DisposeAsync();
                }
            }
        }
    }

    private async Task UploadTrackedAsync(
        string path,
        Stream stream,
        string contentType,
        ICollection<BlobMutation> mutations,
        CancellationToken cancellationToken)
    {
        var existed = await _storageService.ExistsAsync(path);
        BufferedUploadFile backup = null;
        var previousContentType = "application/octet-stream";
        if (existed)
        {
            var properties = await _storageService.GetFileInfoAsync(path)
                ?? throw new IOException($"Blob '{path}' disappeared before it could be backed up.");
            await using var previousStream = await _storageService.OpenReadAsync(path)
                ?? throw new IOException($"Blob '{path}' could not be opened for rollback backup.");
            backup = await BufferUploadFileAsync(previousStream, cancellationToken);
            MediaTransferValidator.RequireComplete(path, properties.Length, backup.Length);
            previousContentType = properties.ContentType;
        }

        mutations.Add(new BlobMutation(path, existed, backup, previousContentType));
        stream.Position = 0;
        await _storageService.UploadAsync(path, stream, contentType);
    }

    public async Task<string> UploadAlbumCoverAsync(
        Stream albumArtStream,
        string albumArtFileName,
        string albumName,
        int? creatorId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(albumArtStream);
        RequireSupportedImageExtension(albumArtFileName);
        if (string.IsNullOrWhiteSpace(albumName))
        {
            throw new ArgumentException("Album name is required.", nameof(albumName));
        }

        await using var buffered = await BufferUploadFileAsync(albumArtStream, cancellationToken);
        await using (var validationStream = buffered.OpenRead())
        {
            ValidateImageContent(validationStream, albumArtFileName);
        }

        var safeAlbumName = SanitizeForPath(albumName);
        var baseName = GetNormalizedBaseName(albumArtFileName);
        var extension = Path.GetExtension(albumArtFileName).ToLowerInvariant();
        var path = $"{safeAlbumName}/{baseName}_cover{extension}";
        await _storageService.EnsureContainerExistsAsync();
        await using (var uploadStream = buffered.OpenRead())
        {
            await _storageService.UploadAsync(
                path,
                uploadStream,
                MusicFileExtensions.GetCoverArtContentType(extension)!);
        }

        await _metadataService.UpsertAsync(new SongMetadata
        {
            BlobPath = path,
            ImageBlobPath = path,
            FileExtension = extension,
            AlbumName = albumName,
            IsAlbumCover = true,
            CreatorId = creatorId
        });
        return path;
    }

    private async Task ValidateAudioContentAsync(Stream stream, string fileName)
    {
        stream.Position = 0;
        if (!await _musicService.IsValidAudioFileAsync(stream, fileName))
        {
            throw new InvalidDataException($"'{fileName}' does not contain audio matching its extension.");
        }
    }

    private static void ValidateImageContent(Stream stream, string fileName)
    {
        stream.Position = 0;
        if (!MediaFileContentValidator.ImageContentMatchesExtension(stream, fileName, out _))
        {
            throw new InvalidDataException($"'{fileName}' does not contain a decodable image matching its extension.");
        }
    }

    private async Task<double> RequirePositiveDurationAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var result = await _musicService.ValidateAudioDecodeAsync(stream, fileName, cancellationToken);
        if (result.Status == AudioDecodeStatus.Inconclusive)
        {
            throw new InvalidOperationException(
                $"'{fileName}' could not be validated because the media decoder was unavailable: {result.FailureCode}.");
        }

        if (result.Status != AudioDecodeStatus.Playable
            || !result.Duration.HasValue
            || result.Duration.Value <= 0)
        {
            throw new InvalidDataException($"'{fileName}' could not be decoded as playable audio.");
        }

        return result.Duration.Value;
    }

    private static void RequireSupportedAudioExtension(string fileName)
    {
        if (!MusicFileExtensions.IsAudioFile(fileName))
        {
            throw new InvalidDataException($"'{fileName}' does not have a supported audio extension.");
        }
    }

    private static void RequireSupportedImageExtension(string fileName)
    {
        if (!MusicFileExtensions.IsCoverArtFile(fileName))
        {
            throw new InvalidDataException($"'{fileName}' does not have a supported cover-art extension.");
        }
    }

    private async Task<BufferedUploadFile> BufferUploadFileAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            await using (var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.CopyToAsync(output, cancellationToken);
            }

            return new BufferedUploadFile(tempPath, new FileInfo(tempPath).Length, _logger);
        }
        catch
        {
            TempFileHelper.TryDelete(tempPath, _logger);
            throw;
        }
    }

    private sealed class BufferedUploadFile(
        string tempPath,
        long length,
        ILogger logger) : IAsyncDisposable
    {
        public long Length { get; } = length;

        public FileStream OpenRead() => new(
            tempPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        public ValueTask DisposeAsync()
        {
            TempFileHelper.TryDelete(tempPath, logger);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record BlobMutation(
        string Path,
        bool Existed,
        BufferedUploadFile Backup,
        string PreviousContentType);

    public bool ValidateFilePairing(string audioFileName, string albumArtFileName)
        => !string.IsNullOrWhiteSpace(audioFileName)
           && !string.IsNullOrWhiteSpace(albumArtFileName)
           && string.Equals(
               GetNormalizedBaseName(audioFileName),
               GetNormalizedBaseName(albumArtFileName),
               StringComparison.OrdinalIgnoreCase);

    public string GetNormalizedBaseName(string fileName)
        => string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));

    public FilePairingValidationResult ValidateAllFilePairings(
        IEnumerable<string> fileNames,
        bool requireAudioFile = true)
        => ValidateAllFilePairings(fileNames, requireAudioFile, requireCoverArt: false);

    public FilePairingValidationResult ValidateAllFilePairings(
        IEnumerable<string> fileNames,
        bool requireAudioFile,
        bool requireCoverArt)
    {
        var result = new FilePairingValidationResult { IsValid = true };
        var files = fileNames?.ToList() ?? [];
        if (files.Count == 0)
        {
            result.IsValid = false;
            return result;
        }

        var invalid = files.Where(file =>
            !MusicFileExtensions.IsAudioFile(file)
            && !MusicFileExtensions.IsCoverArtFile(file)).ToList();
        if (invalid.Count > 0)
        {
            result.IsValid = false;
            result.UnmatchedMp3Files.AddRange(invalid);
            return result;
        }

        var audio = files.Where(MusicFileExtensions.IsAudioFile).ToList();
        var images = files.Where(MusicFileExtensions.IsCoverArtFile).ToList();
        if (!requireAudioFile)
        {
            result.IsValid = images.Count > 0;
            return result;
        }

        if (requireAudioFile && audio.Count == 0)
        {
            result.IsValid = false;
            result.UnmatchedAlbumArtFiles.AddRange(images);
            return result;
        }

        if (requireCoverArt)
        {
            var imageNames = images.Select(GetNormalizedBaseName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.UnmatchedMp3Files.AddRange(audio.Where(item =>
                !imageNames.Contains(GetNormalizedBaseName(item))));
        }

        var audioNames = audio.Select(GetNormalizedBaseName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.UnmatchedAlbumArtFiles.AddRange(images.Where(image =>
            !audioNames.Contains(GetNormalizedBaseName(image))));
        result.IsValid = result.UnmatchedAlbumArtFiles.Count == 0
            && result.UnmatchedMp3Files.Count == 0;
        return result;
    }

    private static string SanitizeForPath(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(input.Where(character => !invalid.Contains(character)).ToArray()).Trim();
    }
}
