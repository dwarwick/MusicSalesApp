using Microsoft.Extensions.Logging;
using MusicSalesApp.Common.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    /// <inheritdoc cref="IMusicService"/>
    public class MusicService : IMusicService
    {
        private readonly ILogger<MusicService> _logger;

        public MusicService(ILogger<MusicService> logger)
        {
            _logger = logger;
        }

        public Task<bool> IsValidAudioFileAsync(Stream fileStream, string fileName)
        {
            if (fileStream == null || string.IsNullOrWhiteSpace(fileName))
            {
                return Task.FromResult(false);
            }

            if (!MusicFileExtensions.IsAudioFile(fileName))
            {
                _logger.LogWarning("File {FileName} does not have a supported audio extension", fileName);
                return Task.FromResult(false);
            }

            var valid = AudioContainerSniffer.ContentMatchesExtension(
                fileStream,
                fileName,
                out var detectedFormat);
            if (!valid)
            {
                _logger.LogWarning(
                    "File {FileName} content did not match its extension. Detected={DetectedFormat}",
                    fileName,
                    detectedFormat);
            }

            return Task.FromResult(valid);
        }

        public bool IsMp3File(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension == ".mp3";
        }
    }
}
