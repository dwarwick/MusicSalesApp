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

        public async Task<double?> GetAudioDurationAsync(Stream audioStream, string fileName)
        {
            if (audioStream == null || string.IsNullOrWhiteSpace(fileName))
                return null;

            string tempInputPath = null;
            try
            {
                // Ensure the stream is at the beginning
                if (audioStream.CanSeek)
                {
                    audioStream.Position = 0;
                }

                // Write the stream to a temporary file for analysis
                tempInputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");
                
                await using (var fileStream = File.Create(tempInputPath))
                {
                    await audioStream.CopyToAsync(fileStream);
                }

                // Use FFMpeg to get duration by processing the file with null output
                // This is more reliable than FFProbe as it uses the same binary
                TimeSpan? duration = null;
                var analysis = await FFMpegArguments
                    .FromFileInput(tempInputPath)
                    .OutputToFile("NUL", true, options => options
                        .WithCustomArgument("-f null"))
                    .NotifyOnProgress(progress =>
                    {
                        // Capture the duration from progress
                        duration = progress;
                    })
                    .ProcessAsynchronously(throwOnError: false);

                if (duration.HasValue && duration.Value.TotalSeconds > 0)
                {
                    return duration.Value.TotalSeconds;
                }

                // Fallback: Try using FFProbe if available
                try
                {
                    var mediaInfo = await FFProbe.AnalyseAsync(tempInputPath);
                    if (mediaInfo?.Duration != null)
                    {
                        return mediaInfo.Duration.TotalSeconds;
                    }
                }
                catch
                {
                    // FFProbe not available, continue without it
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get duration for {FileName}", fileName);
                return null;
            }
            finally
            {
                // Clean up temporary file
                if (!string.IsNullOrEmpty(tempInputPath) && File.Exists(tempInputPath))
                {
                    try
                    {
                        File.Delete(tempInputPath);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }

                // Reset stream position if seekable
                if (audioStream.CanSeek)
                {
                    audioStream.Position = 0;
                }
            }
        }
    }
}