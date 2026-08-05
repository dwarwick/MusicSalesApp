using Microsoft.Extensions.Logging;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc cref="IMusicUploadService"/>
public class MusicUploadService : IMusicUploadService
{
    private readonly IAzureStorageService _storageService;
    private readonly ISongMetadataService _metadataService;
    private readonly ILogger<MusicUploadService> _logger;

    public MusicUploadService(
        IAzureStorageService storageService,
        ISongMetadataService metadataService,
        ILogger<MusicUploadService> logger)
    {
        _storageService = storageService;
        _metadataService = metadataService;
        _logger = logger;
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

    private static void ValidateImageContent(Stream stream, string fileName)
    {
        stream.Position = 0;
        if (!MediaFileContentValidator.ImageContentMatchesExtension(stream, fileName, out _))
        {
            throw new InvalidDataException($"'{fileName}' does not contain a decodable image matching its extension.");
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
