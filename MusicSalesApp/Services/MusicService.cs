using FFMpegCore;
using FFMpegCore.Pipes;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Common.Helpers;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    public class MusicService : IMusicService
    {
        private readonly ILogger<MusicService> _logger;
        private static readonly Regex FfmpegDurationRegex = new(
            @"Duration:\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            if (!MusicFileExtensions.ValidAudioExtensions.Contains(extension))
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

                var tempFileLength = new FileInfo(tempInputPath).Length;
                if (tempFileLength == 0)
                {
                    _logger.LogWarning("Duration extraction copied zero bytes for {FileName}", fileName);
                    return null;
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

                var ffmpegDuration = await GetDurationFromFfmpegOutputAsync(tempInputPath, fileName);
                if (ffmpegDuration.HasValue)
                {
                    return ffmpegDuration.Value;
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

        internal static double? TryParseDurationFromFfmpegOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var match = FfmpegDurationRegex.Match(output);
            if (!match.Success)
            {
                return null;
            }

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) ||
                !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
                !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return null;
            }

            var totalSeconds = TimeSpan.FromHours(hours).TotalSeconds + TimeSpan.FromMinutes(minutes).TotalSeconds + seconds;
            return totalSeconds > 0 ? totalSeconds : null;
        }

        private async Task<double?> GetDurationFromFfmpegOutputAsync(string tempInputPath, string fileName)
        {
            var ffmpegPath = ResolveFfmpegExecutablePath();
            if (ffmpegPath == null)
            {
                _logger.LogWarning("Unable to run ffmpeg duration fallback for {FileName} because ffmpeg.exe was not found.", fileName);
                return null;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -nostdin -i {QuoteProcessArgument(tempInputPath)} -f null -",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var standardErrorTask = process.StandardError.ReadToEndAsync();
                var standardOutputTask = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                var output = await standardErrorTask + await standardOutputTask;
                var duration = TryParseDurationFromFfmpegOutput(output);
                if (duration.HasValue)
                {
                    _logger.LogInformation(
                        "Recovered duration for {FileName} using ffmpeg output fallback. ExitCode={ExitCode}, DurationSeconds={DurationSeconds}",
                        fileName,
                        process.ExitCode,
                        duration.Value);
                }
                else
                {
                    _logger.LogWarning(
                        "ffmpeg output fallback did not find a duration for {FileName}. ExitCode={ExitCode}",
                        fileName,
                        process.ExitCode);
                }

                return duration;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ffmpeg output fallback failed for {FileName}", fileName);
                return null;
            }
        }

        private static string ResolveFfmpegExecutablePath()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg.exe")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static string QuoteProcessArgument(string argument)
        {
            return $"\"{argument.Replace("\"", "\\\"")}\"";
        }
    }
}