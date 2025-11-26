using System;
using System.IO;
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

    }
}