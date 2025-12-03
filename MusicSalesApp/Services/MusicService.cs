using FFMpegCore;
using FFMpegCore.Pipes;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    public class MusicService : IMusicService
    {
        private readonly ILogger<MusicService> _logger;
        private static readonly string[] ValidAudioExtensions = { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };
        private static readonly string[] ValidAudioMimeTypes = {
            "audio/mpeg", "audio/wav", "audio/wave", "audio/x-wav",
            "audio/flac", "audio/ogg", "audio/mp4", "audio/aac",
            "audio/x-ms-wma", "audio/x-m4a"
        };

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

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!ValidAudioExtensions.Contains(extension))
            {
                _logger.LogWarning("File {FileName} has invalid extension {Extension}", fileName, extension);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public async Task<Stream> ConvertToMp3Async(
            Stream inputStream,
            string originalFileName,
            IProgress<double> progress = null)
        {
            if (inputStream == null)
                throw new ArgumentNullException(nameof(inputStream));

            if (string.IsNullOrWhiteSpace(originalFileName))
                throw new ArgumentNullException(nameof(originalFileName));

            try
            {
                var outputStream = new MemoryStream();

                if (inputStream.CanSeek)
                {
                    inputStream.Position = 0;
                }

                var streamPipeSource = new StreamPipeSource(inputStream);
                var streamPipeSink = new StreamPipeSink(outputStream);

                _logger.LogInformation("Starting conversion of {FileName} to MP3", originalFileName);

                var success = await FFMpegArguments
                    .FromPipeInput(streamPipeSource)
                    .OutputToPipe(streamPipeSink, options => options
                        .WithAudioCodec("libmp3lame")
                        .WithAudioBitrate(192)
                        .ForceFormat("mp3"))
                    .NotifyOnProgress(timeSpan =>
                    {
                        if (progress != null)
                        {
                            var percent = Math.Min(100, timeSpan.TotalSeconds * 10);
                            progress.Report(percent);
                        }
                    })
                    .ProcessAsynchronously();

                if (!success)
                {
                    _logger.LogError("Failed to convert {FileName} to MP3", originalFileName);
                    throw new InvalidOperationException($"Failed to convert {originalFileName} to MP3");
                }

                outputStream.Position = 0;
                _logger.LogInformation(
                    "Successfully converted {FileName} to MP3 ({Length} bytes)",
                    originalFileName,
                    outputStream.Length);

                return outputStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error converting {FileName} to MP3", originalFileName);
                throw;
            }
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