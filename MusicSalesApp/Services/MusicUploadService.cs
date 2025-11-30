using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services
{
    public class MusicUploadService : IMusicUploadService
    {
        private readonly IAzureStorageService _storageService;
        private readonly IMusicService _musicService;
        private readonly ILogger<MusicUploadService> _logger;

        private const string MasteredSuffix = "_mastered";
        private static readonly string[] ValidAudioExtensions = { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };
        private static readonly string[] ValidAlbumArtExtensions = { ".jpeg", ".jpg" };

        public MusicUploadService(
            IAzureStorageService storageService,
            IMusicService musicService,
            ILogger<MusicUploadService> logger)
        {
            _storageService = storageService;
            _musicService = musicService;
            _logger = logger;
        }

        /// <summary>
        /// Entry point for MVC controllers: wrap IFormFile and delegate to the stream API.
        /// </summary>
        public async Task<string> UploadAudioAsync(
            IFormFile file,
            string destinationFolder,
            CancellationToken cancellationToken = default)
        {
            if (file == null || file.Length == 0)
            {
                throw new ArgumentException("No file uploaded", nameof(file));
            }

            await using var fileStream = file.OpenReadStream();
            return await UploadAudioAsync(
                fileStream,
                file.FileName,
                destinationFolder,
                cancellationToken);
        }

        /// <summary>
        /// Core implementation used by both MVC and Blazor.
        /// </summary>
        public async Task<string> UploadAudioAsync(
    Stream fileStream,
    string originalFileName,
    string destinationFolder,
    CancellationToken cancellationToken = default)
        {
            if (fileStream == null)
                throw new ArgumentNullException(nameof(fileStream));

            if (string.IsNullOrWhiteSpace(originalFileName))
                throw new ArgumentException("File name is required.", nameof(originalFileName));

            destinationFolder ??= string.Empty;

            // If the incoming stream is not seekable (e.g., BrowserFileStream),
            // buffer it into a MemoryStream so we can rewind / reuse it.
            if (!fileStream.CanSeek)
            {
                var buffered = new MemoryStream();
                await fileStream.CopyToAsync(buffered, cancellationToken);
                buffered.Position = 0;
                fileStream = buffered;
            }

            // Ensure container exists
            await _storageService.EnsureContainerExistsAsync();

            // Validate audio file (this will read from the stream)
            if (!await _musicService.IsValidAudioFileAsync(fileStream, originalFileName))
            {
                throw new InvalidDataException($"File {originalFileName} is not a valid audio file.");
            }

            // Reset stream after validation
            fileStream.Position = 0; // now safe: we know it's seekable

            Stream uploadStream = fileStream;
            string uploadFileName = originalFileName;

            // Convert to MP3 if needed
            if (!_musicService.IsMp3File(originalFileName))
            {
                _logger.LogInformation("Converting {FileName} to MP3", originalFileName);

                uploadStream = await _musicService.ConvertToMp3Async(fileStream, originalFileName);
                uploadFileName = Path.ChangeExtension(originalFileName, ".mp3");
            }

            var fullPath = string.IsNullOrWhiteSpace(destinationFolder)
                ? uploadFileName
                : $"{destinationFolder.TrimEnd('/')}/{uploadFileName}";

            try
            {
                await _storageService.UploadAsync(fullPath, uploadStream, "audio/mpeg");
            }
            finally
            {
                // Dispose only the converted stream; caller owns the original.
                if (!ReferenceEquals(uploadStream, fileStream))
                {
                    await uploadStream.DisposeAsync();
                }
            }

            return fullPath;
        }

        /// <inheritdoc />
        public async Task<string> UploadMusicWithAlbumArtAsync(
            Stream audioStream,
            string audioFileName,
            Stream albumArtStream,
            string albumArtFileName,
            string albumName = null,
            CancellationToken cancellationToken = default)
        {
            if (audioStream == null)
                throw new ArgumentNullException(nameof(audioStream));
            if (albumArtStream == null)
                throw new ArgumentNullException(nameof(albumArtStream));
            if (string.IsNullOrWhiteSpace(audioFileName))
                throw new ArgumentException("Audio file name is required.", nameof(audioFileName));
            if (string.IsNullOrWhiteSpace(albumArtFileName))
                throw new ArgumentException("Album art file name is required.", nameof(albumArtFileName));

            // Validate file pairing
            if (!ValidateFilePairing(audioFileName, albumArtFileName))
            {
                throw new InvalidOperationException(
                    $"Filenames do not match: '{audioFileName}' and '{albumArtFileName}'. " +
                    "MP3 and album art files must have the same base name.");
            }

            // Get the normalized base name for folder and file naming
            var baseName = GetNormalizedBaseName(audioFileName);

            // Buffer streams if needed
            if (!audioStream.CanSeek)
            {
                var buffered = new MemoryStream();
                await audioStream.CopyToAsync(buffered, cancellationToken);
                buffered.Position = 0;
                audioStream = buffered;
            }

            if (!albumArtStream.CanSeek)
            {
                var buffered = new MemoryStream();
                await albumArtStream.CopyToAsync(buffered, cancellationToken);
                buffered.Position = 0;
                albumArtStream = buffered;
            }

            // Ensure container exists
            await _storageService.EnsureContainerExistsAsync();

            // Validate audio file
            if (!await _musicService.IsValidAudioFileAsync(audioStream, audioFileName))
            {
                throw new InvalidDataException($"File {audioFileName} is not a valid audio file.");
            }

            audioStream.Position = 0;

            Stream uploadAudioStream = audioStream;
            string mp3FileName = baseName + ".mp3";

            // Convert to MP3 if needed
            if (!_musicService.IsMp3File(audioFileName))
            {
                _logger.LogInformation("Converting {FileName} to MP3", audioFileName);
                uploadAudioStream = await _musicService.ConvertToMp3Async(audioStream, audioFileName);
            }

            // Create folder path and file paths
            string folderPath = baseName;
            string mp3Path = $"{folderPath}/{mp3FileName}";
            string albumArtPath = $"{folderPath}/{baseName}.jpeg";

            // Build metadata for audio file
            Dictionary<string, string> audioMetadata = null;
            if (!string.IsNullOrWhiteSpace(albumName))
            {
                audioMetadata = new Dictionary<string, string>
                {
                    { MetadataNames.AlbumName, albumName }
                };
            }

            // Build metadata for album art file
            Dictionary<string, string> albumArtMetadata = null;
            if (!string.IsNullOrWhiteSpace(albumName))
            {
                albumArtMetadata = new Dictionary<string, string>
                {
                    { MetadataNames.AlbumName, albumName },
                    { MetadataNames.IsAlbumCover, "false" }
                };
            }

            try
            {
                // Upload MP3 file
                _logger.LogInformation("Uploading MP3 file to {Path}", mp3Path);
                await _storageService.UploadAsync(mp3Path, uploadAudioStream, "audio/mpeg", audioMetadata);

                // Upload album art
                _logger.LogInformation("Uploading album art to {Path}", albumArtPath);
                albumArtStream.Position = 0;
                await _storageService.UploadAsync(albumArtPath, albumArtStream, "image/jpeg", albumArtMetadata);

                _logger.LogInformation("Successfully uploaded music and album art to folder {Folder}", folderPath);
            }
            finally
            {
                // Dispose only the converted stream; caller owns the original.
                if (!ReferenceEquals(uploadAudioStream, audioStream))
                {
                    await uploadAudioStream.DisposeAsync();
                }
            }

            return folderPath;
        }

        /// <inheritdoc />
        public async Task<string> UploadAlbumCoverAsync(
            Stream albumArtStream,
            string albumArtFileName,
            string albumName,
            CancellationToken cancellationToken = default)
        {
            if (albumArtStream == null)
                throw new ArgumentNullException(nameof(albumArtStream));
            if (string.IsNullOrWhiteSpace(albumArtFileName))
                throw new ArgumentException("Album art file name is required.", nameof(albumArtFileName));
            if (string.IsNullOrWhiteSpace(albumName))
                throw new ArgumentException("Album name is required.", nameof(albumName));

            // Validate file extension
            if (!IsAlbumArtFile(albumArtFileName))
            {
                throw new InvalidDataException($"File {albumArtFileName} is not a valid album art file. Accepted formats: JPEG, JPG.");
            }

            // Buffer stream if needed
            if (!albumArtStream.CanSeek)
            {
                var buffered = new MemoryStream();
                await albumArtStream.CopyToAsync(buffered, cancellationToken);
                buffered.Position = 0;
                albumArtStream = buffered;
            }

            // Ensure container exists
            await _storageService.EnsureContainerExistsAsync();

            // Create a sanitized album name for the folder/file path
            var sanitizedAlbumName = SanitizeForPath(albumName);
            var baseName = GetNormalizedBaseName(albumArtFileName);
            string albumCoverPath = $"{sanitizedAlbumName}/{baseName}_cover.jpeg";

            // Build metadata for album cover
            var metadata = new Dictionary<string, string>
            {
                { MetadataNames.AlbumName, albumName },
                { MetadataNames.IsAlbumCover, "true" }
            };

            // Upload album cover
            _logger.LogInformation("Uploading album cover to {Path}", albumCoverPath);
            albumArtStream.Position = 0;
            await _storageService.UploadAsync(albumCoverPath, albumArtStream, "image/jpeg", metadata);

            _logger.LogInformation("Successfully uploaded album cover for album {AlbumName}", albumName);

            return albumCoverPath;
        }

        /// <summary>
        /// Sanitizes a string to be used in a file path by removing invalid characters.
        /// </summary>
        private static string SanitizeForPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(input.Where(c => !invalidChars.Contains(c)).ToArray());
            return sanitized.Trim();
        }

        /// <inheritdoc />
        public bool ValidateFilePairing(string audioFileName, string albumArtFileName)
        {
            if (string.IsNullOrWhiteSpace(audioFileName) || string.IsNullOrWhiteSpace(albumArtFileName))
                return false;

            var audioBaseName = GetNormalizedBaseName(audioFileName);
            var albumArtBaseName = GetNormalizedBaseName(albumArtFileName);

            return string.Equals(audioBaseName, albumArtBaseName, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public string GetNormalizedBaseName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            // Get filename without extension
            var baseName = Path.GetFileNameWithoutExtension(fileName);

            // Remove "_mastered" suffix if present (case-insensitive)
            if (baseName.EndsWith(MasteredSuffix, StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName.Substring(0, baseName.Length - MasteredSuffix.Length);
            }

            return baseName;
        }

        /// <inheritdoc />
        public FilePairingValidationResult ValidateAllFilePairings(IEnumerable<string> fileNames)
        {
            return ValidateAllFilePairings(fileNames, requireAudioFile: true);
        }

        /// <inheritdoc />
        public FilePairingValidationResult ValidateAllFilePairings(IEnumerable<string> fileNames, bool requireAudioFile = true)
        {
            var result = new FilePairingValidationResult { IsValid = true };

            if (fileNames == null || !fileNames.Any())
            {
                result.IsValid = false;
                return result;
            }

            var fileList = fileNames.ToList();

            // Separate audio files from album art files
            var audioFiles = fileList.Where(f => IsAudioFile(f)).ToList();
            var albumArtFiles = fileList.Where(f => IsAlbumArtFile(f)).ToList();

            // For album cover upload, we only need album art files (no audio required)
            if (!requireAudioFile)
            {
                // Valid if we have at least one album art file
                if (!albumArtFiles.Any())
                {
                    result.IsValid = false;
                    // No album art files found - nothing to add to unmatched since list is empty
                    return result;
                }

                // All album art files are valid for album cover upload
                return result;
            }

            // Check if there are no files of either type
            if (!audioFiles.Any() || !albumArtFiles.Any())
            {
                result.IsValid = false;
                result.UnmatchedMp3Files.AddRange(audioFiles);
                result.UnmatchedAlbumArtFiles.AddRange(albumArtFiles);
                return result;
            }

            // Get normalized base names for each type
            var audioBaseNames = audioFiles
                .ToDictionary(f => GetNormalizedBaseName(f).ToLowerInvariant(), f => f);
            var albumArtBaseNames = albumArtFiles
                .ToDictionary(f => GetNormalizedBaseName(f).ToLowerInvariant(), f => f);

            // Find unmatched audio files
            foreach (var audio in audioBaseNames)
            {
                if (!albumArtBaseNames.ContainsKey(audio.Key))
                {
                    result.UnmatchedMp3Files.Add(audio.Value);
                }
            }

            // Find unmatched album art files
            foreach (var art in albumArtBaseNames)
            {
                if (!audioBaseNames.ContainsKey(art.Key))
                {
                    result.UnmatchedAlbumArtFiles.Add(art.Value);
                }
            }

            result.IsValid = !result.UnmatchedMp3Files.Any() && !result.UnmatchedAlbumArtFiles.Any();

            return result;
        }

        private bool IsAudioFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return ValidAudioExtensions.Contains(extension);
        }

        private bool IsAlbumArtFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return ValidAlbumArtExtensions.Contains(extension);
        }
    }
}