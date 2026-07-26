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
        private static readonly Regex FfmpegProgressTimeRegex = new(
            @"out_time=(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(10);

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

            var valid = MediaFileContentValidator.AudioContentMatchesExtension(
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
            var result = await ValidateAudioDecodeAsync(audioStream, fileName);
            return result.Status == AudioDecodeStatus.Playable ? result.Duration : null;
        }

        public async Task<AudioDecodeResult> ValidateAudioDecodeAsync(
            Stream audioStream,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            if (audioStream == null || string.IsNullOrWhiteSpace(fileName))
                return AudioDecodeResult.Unplayable("MissingInput", "No audio stream or filename was supplied.");

            var ffmpegPath = ResolveFfmpegExecutablePath();
            if (ffmpegPath == null)
            {
                return AudioDecodeResult.Inconclusive(
                    "FfmpegUnavailable",
                    "FFmpeg is not installed or could not be located on this worker.");
            }

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!MusicFileExtensions.ValidAudioExtensions.Contains(extension))
                extension = ".bin";
            var tempInputPath = Path.Combine(Path.GetTempPath(), $"media-decode-{Guid.NewGuid():N}{extension}");
            Process process = null;
            try
            {
                if (audioStream.CanSeek)
                    audioStream.Position = 0;

                await using (var output = new FileStream(
                    tempInputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await audioStream.CopyToAsync(output, cancellationToken);
                }

                if (new FileInfo(tempInputPath).Length == 0)
                    return AudioDecodeResult.Unplayable("EmptyFile", "The audio file is empty.");

                var nullTarget = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -nostdin -xerror -progress pipe:1 -nostats "
                        + $"-i {QuoteProcessArgument(tempInputPath)} -map 0:a:0 -vn -f null {QuoteProcessArgument(nullTarget)}",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                process = new Process { StartInfo = startInfo };
                process.Start();
                var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DecodeTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    return AudioDecodeResult.Inconclusive(
                        "DecoderTimeout",
                        $"FFmpeg did not finish within {DecodeTimeout.TotalMinutes:0} minutes.");
                }

                var standardError = await standardErrorTask;
                var standardOutput = await standardOutputTask;
                var diagnostic = SanitizeDecoderDiagnostic(standardError);
                if (process.ExitCode != 0)
                {
                    return IsInfrastructureDiagnostic(standardError)
                        ? AudioDecodeResult.Inconclusive("DecoderInfrastructureFailure", diagnostic)
                        : AudioDecodeResult.Unplayable("DecoderRejected", diagnostic);
                }

                var duration = TryParseProgressDuration(standardOutput)
                    ?? TryParseDurationFromFfmpegOutput(standardError);
                if (!duration.HasValue || duration <= 0)
                {
                    return AudioDecodeResult.Unplayable(
                        "NoPositiveAudioDuration",
                        "FFmpeg completed without producing a positive-duration audio stream.");
                }

                return AudioDecodeResult.Playable(duration.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw;
            }
            catch (IOException ex)
            {
                return AudioDecodeResult.Inconclusive("LocalIoFailure", SanitizeDecoderDiagnostic(ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                return AudioDecodeResult.Inconclusive("LocalAccessFailure", SanitizeDecoderDiagnostic(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to execute FFmpeg validation for {FileName}", fileName);
                return AudioDecodeResult.Inconclusive("DecoderInfrastructureFailure", SanitizeDecoderDiagnostic(ex.Message));
            }
            finally
            {
                process?.Dispose();
                TempFileHelper.TryDelete(tempInputPath, _logger);
                if (audioStream.CanSeek)
                    audioStream.Position = 0;
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

        internal static double? TryParseProgressDuration(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            var matches = FfmpegProgressTimeRegex.Matches(output);
            if (matches.Count == 0)
                return null;
            var match = matches[^1];
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
                || !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                || !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return null;

            var total = TimeSpan.FromHours(hours).TotalSeconds
                + TimeSpan.FromMinutes(minutes).TotalSeconds
                + seconds;
            return total > 0 ? total : null;
        }

        private static bool IsInfrastructureDiagnostic(string diagnostic)
            => diagnostic.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("Cannot allocate memory", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("Resource temporarily unavailable", StringComparison.OrdinalIgnoreCase);

        private static string SanitizeDecoderDiagnostic(string diagnostic)
        {
            if (string.IsNullOrWhiteSpace(diagnostic))
                return "FFmpeg rejected the file without a diagnostic.";
            var sanitized = diagnostic.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return sanitized.Length <= 2000 ? sanitized : sanitized[..2000];
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort only.
            }
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
