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
        private readonly ISongMetadataService _metadataService;
        private readonly ILogger<MusicUploadService> _logger;

        private const string MasteredSuffix = "_mastered";

        public MusicUploadService(
            IAzureStorageService storageService,
            IMusicService musicService,
            ISongMetadataService metadataService,
            ILogger<MusicUploadService> logger)
        {
            _storageService = storageService;
            _musicService = musicService;
            _metadataService = metadataService;
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
            // buffer it into a temp file so we can rewind / reuse it without
            // holding the entire file in memory.
            string tempFilePath = null;
            if (!fileStream.CanSeek)
            {
                tempFilePath = Path.GetTempFileName();
                await using (var tempFs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await fileStream.CopyToAsync(tempFs, cancellationToken);
                }
                fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            }

            try
            {
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
            finally
            {
                // If we created a temp-file-backed FileStream, dispose it and delete the temp file.
                if (tempFilePath != null)
                {
                    await fileStream.DisposeAsync();
                    TempFileHelper.TryDelete(tempFilePath, _logger);
                }
            }
        }

        /// <inheritdoc />
        public async Task<string> UploadMusicWithAlbumArtAsync(
            Stream audioStream,
            string audioFileName,
            Stream albumArtStream,
            string albumArtFileName,
            string albumName = null,
            int? creatorId = null,
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
            if (creatorId == null)
            {
                _logger.LogError("MusicUploadService: CreatorId is null for upload of {AudioFileName} + {AlbumArtFileName}. Songs must have a creator.", audioFileName, albumArtFileName);
                throw new InvalidOperationException("Cannot upload music without a creator ID. Please ensure you are logged in as a creator.");
            }

            // Validate file pairing
            if (!ValidateFilePairing(audioFileName, albumArtFileName))
            {
                throw new InvalidOperationException(
                    $"Filenames do not match: '{audioFileName}' and '{albumArtFileName}'. " +
                    "MP3 and album art files must have the same base name.");
            }

            // Get the normalized base name for folder and file naming
            var baseName = GetNormalizedBaseName(audioFileName);

            // Buffer non-seekable streams into temp files so we can rewind / reuse them
            // without holding entire files in memory.
            string audioTempPath = null;
            if (!audioStream.CanSeek)
            {
                audioTempPath = Path.GetTempFileName();
                await using (var tempFs = new FileStream(audioTempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await audioStream.CopyToAsync(tempFs, cancellationToken);
                }
                audioStream = new FileStream(audioTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            }

            string albumArtTempPath = null;
            if (!albumArtStream.CanSeek)
            {
                albumArtTempPath = Path.GetTempFileName();
                await using (var tempFs = new FileStream(albumArtTempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await albumArtStream.CopyToAsync(tempFs, cancellationToken);
                }
                albumArtStream = new FileStream(albumArtTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            }

            try
            {

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

            // Preserve original image file extension
            var albumArtExtension = Path.GetExtension(albumArtFileName).ToLowerInvariant();
            string albumArtPath = $"{folderPath}/{baseName}{albumArtExtension}";

            // Determine content type based on extension
            string imageContentType = GetImageContentType(albumArtExtension);

            // Get track duration from the MP3 file (after conversion if needed)
            double? trackDuration = null;
            try
            {
                uploadAudioStream.Position = 0;
                trackDuration = await _musicService.GetAudioDurationAsync(uploadAudioStream, mp3FileName);
                uploadAudioStream.Position = 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract track duration for {FileName}", mp3FileName);
            }

            try
            {
                // Upload MP3 file (no tags)
                _logger.LogInformation("Uploading MP3 file to {Path}", mp3Path);
                await _storageService.UploadAsync(mp3Path, uploadAudioStream, "audio/mpeg");

                // Upload album art (no tags)
                _logger.LogInformation("Uploading album art to {Path}", albumArtPath);
                albumArtStream.Position = 0;
                await _storageService.UploadAsync(albumArtPath, albumArtStream, imageContentType);

                // Save metadata to database - single record with both MP3 and image paths
                await _metadataService.UpsertAsync(new Models.SongMetadata
                {
                    BlobPath = mp3Path, // Kept for backward compatibility
                    Mp3BlobPath = mp3Path,
                    ImageBlobPath = albumArtPath,
                    FileExtension = ".mp3",
                    AlbumName = albumName ?? string.Empty,
                    IsAlbumCover = false,
                    TrackLength = trackDuration,
                    CreatorId = creatorId
                });

                _logger.LogInformation("Successfully uploaded music and album art to folder {Folder} with CreatorId={CreatorId}", folderPath, creatorId);
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
            finally
            {
                // If we created temp-file-backed FileStreams, dispose them and delete the temp files.
                if (audioTempPath != null)
                {
                    await audioStream.DisposeAsync();
                    TempFileHelper.TryDelete(audioTempPath, _logger);
                }
                if (albumArtTempPath != null)
                {
                    await albumArtStream.DisposeAsync();
                    TempFileHelper.TryDelete(albumArtTempPath, _logger);
                }
            }
        }

        /// <inheritdoc />
        public async Task<string> UploadMusicWithoutAlbumArtAsync(
            Stream audioStream,
            string audioFileName,
            string albumName = null,
            int? creatorId = null,
            CancellationToken cancellationToken = default)
        {
            if (audioStream == null)
                throw new ArgumentNullException(nameof(audioStream));
            if (string.IsNullOrWhiteSpace(audioFileName))
                throw new ArgumentException("Audio file name is required.", nameof(audioFileName));
            if (creatorId == null)
            {
                _logger.LogError("MusicUploadService: CreatorId is null for upload of {AudioFileName}. Songs must have a creator.", audioFileName);
                throw new InvalidOperationException("Cannot upload music without a creator ID. Please ensure you are logged in as a creator.");
            }

            // Get the normalized base name for folder and file naming
            var baseName = GetNormalizedBaseName(audioFileName);

            // Buffer non-seekable stream into a temp file so we can rewind / reuse it
            // without holding the entire file in memory.
            string audioTempPath = null;
            if (!audioStream.CanSeek)
            {
                audioTempPath = Path.GetTempFileName();
                await using (var tempFs = new FileStream(audioTempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await audioStream.CopyToAsync(tempFs, cancellationToken);
                }
                audioStream = new FileStream(audioTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            }

            try
            {

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

            // Get track duration from the MP3 file (after conversion if needed)
            double? trackDuration = null;
            try
            {
                uploadAudioStream.Position = 0;
                trackDuration = await _musicService.GetAudioDurationAsync(uploadAudioStream, mp3FileName);
                uploadAudioStream.Position = 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract track duration for {FileName}", mp3FileName);
            }

            try
            {
                // Upload MP3 file (no tags)
                _logger.LogInformation("Uploading MP3 file to {Path}", mp3Path);
                await _storageService.UploadAsync(mp3Path, uploadAudioStream, "audio/mpeg");

                // Save metadata to database - single record with MP3 path only, no image path
                await _metadataService.UpsertAsync(new Models.SongMetadata
                {
                    BlobPath = mp3Path, // Kept for backward compatibility
                    Mp3BlobPath = mp3Path,
                    ImageBlobPath = null, // No cover art
                    FileExtension = ".mp3",
                    AlbumName = albumName ?? string.Empty,
                    IsAlbumCover = false,
                    TrackLength = trackDuration,
                    CreatorId = creatorId
                });

                _logger.LogInformation("Successfully uploaded music without album art to folder {Folder} with CreatorId={CreatorId}", folderPath, creatorId);
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
            finally
            {
                // If we created a temp-file-backed FileStream, dispose it and delete the temp file.
                if (audioTempPath != null)
                {
                    await audioStream.DisposeAsync();
                    TempFileHelper.TryDelete(audioTempPath, _logger);
                }
            }
        }

        /// <inheritdoc />
        public async Task<string> UploadAlbumCoverAsync(
            Stream albumArtStream,
            string albumArtFileName,
            string albumName,
            int? creatorId = null,
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
                throw new InvalidDataException($"File {albumArtFileName} is not a valid album art file. Accepted formats: JPEG, JPG, PNG.");
            }

            // Buffer non-seekable stream into a temp file so we can rewind / reuse it
            // without holding the entire file in memory.
            string albumArtTempPath = null;
            if (!albumArtStream.CanSeek)
            {
                albumArtTempPath = Path.GetTempFileName();
                await using (var tempFs = new FileStream(albumArtTempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await albumArtStream.CopyToAsync(tempFs, cancellationToken);
                }
                albumArtStream = new FileStream(albumArtTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            }

            // Ensure container exists
            await _storageService.EnsureContainerExistsAsync();

            // Create a sanitized album name for the folder/file path
            var sanitizedAlbumName = SanitizeForPath(albumName);
            var baseName = GetNormalizedBaseName(albumArtFileName);
            
            // Preserve original image file extension
            var albumArtExtension = Path.GetExtension(albumArtFileName).ToLowerInvariant();
            string albumCoverPath = $"{sanitizedAlbumName}/{baseName}_cover{albumArtExtension}";
            
            // Determine content type based on extension
            string imageContentType = GetImageContentType(albumArtExtension);

            // Upload album cover (no tags)
            _logger.LogInformation("Uploading album cover to {Path}", albumCoverPath);
            try
            {
                albumArtStream.Position = 0;
                await _storageService.UploadAsync(albumCoverPath, albumArtStream, imageContentType);

                // Save metadata to database
                await _metadataService.UpsertAsync(new Models.SongMetadata
                {
                    BlobPath = albumCoverPath, // Kept for backward compatibility
                    Mp3BlobPath = null, // No MP3 for album cover
                    ImageBlobPath = albumCoverPath,
                    FileExtension = albumArtExtension,
                    AlbumName = albumName,
                    IsAlbumCover = true,
                    CreatorId = creatorId
                });

                _logger.LogInformation("Successfully uploaded album cover for album {AlbumName}", albumName);
            }
            finally
            {
                // If we created a temp-file-backed FileStream, dispose it and delete the temp file.
                if (albumArtTempPath != null)
                {
                    await albumArtStream.DisposeAsync();
                    TempFileHelper.TryDelete(albumArtTempPath, _logger);
                }
            }

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
            return ValidateAllFilePairings(fileNames, requireAudioFile: true, requireCoverArt: false);
        }

        /// <inheritdoc />
        public FilePairingValidationResult ValidateAllFilePairings(IEnumerable<string> fileNames, bool requireAudioFile = true)
        {
            return ValidateAllFilePairings(fileNames, requireAudioFile, requireCoverArt: false);
        }

        /// <summary>
        /// Validates file pairings with configurable requirements.
        /// </summary>
        /// <param name="fileNames">List of filenames to validate.</param>
        /// <param name="requireAudioFile">If true, requires at least one audio file.</param>
        /// <param name="requireCoverArt">If true, requires each audio file to have matching cover art.</param>
        /// <returns>A result containing unmatched files if validation fails.</returns>
        public FilePairingValidationResult ValidateAllFilePairings(IEnumerable<string> fileNames, bool requireAudioFile, bool requireCoverArt)
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

            // Audio files are required
            if (!audioFiles.Any())
            {
                result.IsValid = false;
                result.UnmatchedAlbumArtFiles.AddRange(albumArtFiles);
                return result;
            }

            // If cover art is not required, audio-only uploads are valid
            if (!requireCoverArt)
            {
                // Get normalized base names for each type
                var audioBaseNames = audioFiles
                    .ToDictionary(f => GetNormalizedBaseName(f).ToLowerInvariant(), f => f);
                var albumArtBaseNames = albumArtFiles
                    .ToDictionary(f => GetNormalizedBaseName(f).ToLowerInvariant(), f => f);

                // Find unmatched album art files (album art without audio is invalid)
                foreach (var art in albumArtBaseNames)
                {
                    if (!audioBaseNames.ContainsKey(art.Key))
                    {
                        result.UnmatchedAlbumArtFiles.Add(art.Value);
                    }
                }

                // Audio files without cover art are valid - don't add to unmatched
                result.IsValid = !result.UnmatchedAlbumArtFiles.Any();
                return result;
            }

            // Cover art is required - check if there are album art files
            if (!albumArtFiles.Any())
            {
                result.IsValid = false;
                result.UnmatchedMp3Files.AddRange(audioFiles);
                return result;
            }

            // Get normalized base names for each type
            var audioBaseNamesMap = audioFiles
                .ToDictionary(f => GetNormalizedBaseName(f).ToLowerInvariant(), f => f);
            var albumArtBaseNamesMap = albumArtFiles
                .ToDictionary(f => GetNormalizedBaseName(f).ToLowerInvariant(), f => f);

            // Find unmatched audio files
            foreach (var audio in audioBaseNamesMap)
            {
                if (!albumArtBaseNamesMap.ContainsKey(audio.Key))
                {
                    result.UnmatchedMp3Files.Add(audio.Value);
                }
            }

            // Find unmatched album art files
            foreach (var art in albumArtBaseNamesMap)
            {
                if (!audioBaseNamesMap.ContainsKey(art.Key))
                {
                    result.UnmatchedAlbumArtFiles.Add(art.Value);
                }
            }

            result.IsValid = !result.UnmatchedMp3Files.Any() && !result.UnmatchedAlbumArtFiles.Any();

            return result;
        }

        private static bool IsAudioFile(string fileName)
            => MusicFileExtensions.IsAudioFile(fileName);

        private static bool IsAlbumArtFile(string fileName)
            => MusicFileExtensions.IsCoverArtFile(fileName);

        private static string GetImageContentType(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                _ => "image/jpeg" // Default fallback
            };
        }
    }
}