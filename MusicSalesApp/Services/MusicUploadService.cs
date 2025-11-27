using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MusicSalesApp.Services
{
    public class MusicUploadService : IMusicUploadService
    {
        private readonly IAzureStorageService _storageService;
        private readonly IMusicService _musicService;
        private readonly ILogger<MusicUploadService> _logger;

        private const string MasteredSuffix = "_mastered";
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

            try
            {
                // Upload MP3 file
                _logger.LogInformation("Uploading MP3 file to {Path}", mp3Path);
                await _storageService.UploadAsync(mp3Path, uploadAudioStream, "audio/mpeg");

                // Upload album art
                _logger.LogInformation("Uploading album art to {Path}", albumArtPath);
                albumArtStream.Position = 0;
                await _storageService.UploadAsync(albumArtPath, albumArtStream, "image/jpeg");

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
            var result = new FilePairingValidationResult { IsValid = true };

            if (fileNames == null || !fileNames.Any())
            {
                result.IsValid = false;
                return result;
            }

            var fileList = fileNames.ToList();

            // Separate MP3 files from album art files
            var mp3Files = fileList.Where(f => IsMp3File(f)).ToList();
            var albumArtFiles = fileList.Where(f => IsAlbumArtFile(f)).ToList();

            // Check if there are no files of either type
            if (!mp3Files.Any() || !albumArtFiles.Any())
            {
                result.IsValid = false;
                result.UnmatchedMp3Files.AddRange(mp3Files);
                result.UnmatchedAlbumArtFiles.AddRange(albumArtFiles);
                return result;
            }

            // Get normalized base names for each type
            var mp3BaseNames = mp3Files
                .ToDictionary(f => GetNormalizedBaseName(f).ToLowerInvariant(), f => f);
            var albumArtBaseNames = albumArtFiles
                .ToDictionary(f => GetNormalizedBaseName(f).ToLowerInvariant(), f => f);

            // Find unmatched MP3 files
            foreach (var mp3 in mp3BaseNames)
            {
                if (!albumArtBaseNames.ContainsKey(mp3.Key))
                {
                    result.UnmatchedMp3Files.Add(mp3.Value);
                }
            }

            // Find unmatched album art files
            foreach (var art in albumArtBaseNames)
            {
                if (!mp3BaseNames.ContainsKey(art.Key))
                {
                    result.UnmatchedAlbumArtFiles.Add(art.Value);
                }
            }

            result.IsValid = !result.UnmatchedMp3Files.Any() && !result.UnmatchedAlbumArtFiles.Any();

            return result;
        }

        private bool IsMp3File(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension == ".mp3";
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