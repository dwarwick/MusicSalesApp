using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FFMpegCore;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Functions.Audio;

/// <summary>
/// Every FFmpeg invocation in the system.
///
/// <para>
/// Lifted from the web app's <c>MusicService</c> when audio processing moved to Azure Functions. The
/// behaviour is deliberately unchanged — libmp3lame at CBR 192k, a full decode to prove playability
/// and measure duration, and the same classification of infrastructure failures as
/// <see cref="AudioDecodeStatus.Inconclusive"/> rather than blaming the file.
/// </para>
/// </summary>
public interface IFfmpegAudioProcessor
{
    /// <summary>
    /// Transcodes to MP3 on disk.
    ///
    /// <para>
    /// File-to-file rather than the web app's pipe-to-<c>MemoryStream</c>: Consumption instances
    /// have 1.5 GB of RAM and a 100 MB source would otherwise be held in memory twice over.
    /// </para>
    /// </summary>
    /// <param name="sourceDurationSeconds">
    /// The source's measured length, used to turn FFmpeg's progress callback into a real
    /// percentage. The web app's version had no duration to hand and reported a fabricated
    /// <c>elapsed * 10</c> instead; here the source is always probed first, so the figure is honest.
    /// </param>
    Task TranscodeToMp3Async(
        string sourcePath,
        string destinationPath,
        double? sourceDurationSeconds,
        IProgress<double> progress,
        CancellationToken cancellationToken = default);

    /// <summary>Fully decodes a file to prove it is playable and to measure its duration.</summary>
    Task<AudioDecodeResult> DecodeAsync(
        string path,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class FfmpegAudioProcessor : IFfmpegAudioProcessor
{
    private static readonly Regex FfmpegDurationRegex = new(
        @"Duration:\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FfmpegProgressTimeRegex = new(
        @"out_time=(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches the host's own ceiling. Windows Consumption kills an execution at 10 minutes, so a
    /// decode that outlives this was never going to finish anyway.
    /// </summary>
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(10);

    private readonly ILogger<FfmpegAudioProcessor> _logger;

    public FfmpegAudioProcessor(ILogger<FfmpegAudioProcessor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task TranscodeToMp3Async(
        string sourcePath,
        string destinationPath,
        double? sourceDurationSeconds,
        IProgress<double> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        _logger.LogInformation("Transcoding {SourcePath} to MP3", Path.GetFileName(sourcePath));

        var arguments = FFMpegArguments
            .FromFileInput(sourcePath)
            .OutputToFile(destinationPath, overwrite: true, options => options
                .WithAudioCodec("libmp3lame")
                .WithAudioBitrate(192)
                .ForceFormat("mp3"));

        if (progress is not null && sourceDurationSeconds is > 0)
        {
            arguments = arguments.NotifyOnProgress(
                processed => progress.Report(
                    Math.Clamp(processed.TotalSeconds / sourceDurationSeconds.Value * 100d, 0d, 100d)));
        }

        var success = await arguments.CancellableThrough(cancellationToken).ProcessAsynchronously(throwOnError: false);

        if (!success)
        {
            throw new InvalidOperationException($"FFmpeg failed to transcode '{Path.GetFileName(sourcePath)}' to MP3.");
        }

        _logger.LogInformation(
            "Transcoded {SourcePath} to MP3 ({Length} bytes)",
            Path.GetFileName(sourcePath),
            new FileInfo(destinationPath).Length);
    }

    /// <inheritdoc />
    public async Task<AudioDecodeResult> DecodeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return AudioDecodeResult.Unplayable("MissingInput", "No audio file was supplied.");
        }

        var ffmpegPath = ResolveFfmpegExecutablePath();
        if (ffmpegPath is null)
        {
            return AudioDecodeResult.Inconclusive(
                "FfmpegUnavailable",
                "FFmpeg is not installed or could not be located on this worker.");
        }

        if (!File.Exists(path))
        {
            return AudioDecodeResult.Unplayable("MissingInput", "The audio file does not exist.");
        }

        if (new FileInfo(path).Length == 0)
        {
            return AudioDecodeResult.Unplayable("EmptyFile", "The audio file is empty.");
        }

        Process process = null;
        try
        {
            // -f null discards the decoded output: the point is to prove every frame decodes, not
            // to produce anything. -progress pipe:1 gives out_time on stdout, which is a more
            // reliable duration than the banner's Duration: line for a file with a broken header.
            var nullTarget = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -nostdin -xerror -progress pipe:1 -nostats "
                    + $"-i {QuoteProcessArgument(path)} -map 0:a:0 -vn -f null {QuoteProcessArgument(nullTarget)}",
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
                // The distinction that keeps a disk-full worker from condemning good songs.
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
            _logger.LogWarning(ex, "Unable to execute FFmpeg validation for {Path}", path);
            return AudioDecodeResult.Inconclusive(
                "DecoderInfrastructureFailure",
                SanitizeDecoderDiagnostic(ex.Message));
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Locates the bundled binary.
    ///
    /// <para>
    /// The deployment package is mounted read-only under <c>WEBSITE_RUN_FROM_PACKAGE=1</c>, which is
    /// fine for execution — only writing beside it would fail. Both a Windows and an extensionless
    /// name are probed so this keeps working if the app is ever moved to a Linux plan.
    /// </para>
    /// </summary>
    internal static string ResolveFfmpegExecutablePath()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "ffmpeg.exe" }
            : new[] { "ffmpeg", "ffmpeg.exe" };

        foreach (var directory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
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

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var totalSeconds = TimeSpan.FromHours(hours).TotalSeconds
            + TimeSpan.FromMinutes(minutes).TotalSeconds
            + seconds;
        return totalSeconds > 0 ? totalSeconds : null;
    }

    internal static double? TryParseProgressDuration(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var matches = FfmpegProgressTimeRegex.Matches(output);
        if (matches.Count == 0)
        {
            return null;
        }

        // The last out_time reported is the point the decode actually reached, which is the real
        // duration - an earlier one would just be a checkpoint partway through.
        var match = matches[^1];
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var total = TimeSpan.FromHours(hours).TotalSeconds
            + TimeSpan.FromMinutes(minutes).TotalSeconds
            + seconds;
        return total > 0 ? total : null;
    }

    /// <summary>
    /// Whether FFmpeg's complaint is about the machine rather than the file. These are the cases
    /// where retrying is right and condemning the upload would be wrong.
    /// </summary>
    internal static bool IsInfrastructureDiagnostic(string diagnostic)
        => !string.IsNullOrEmpty(diagnostic)
           && (diagnostic.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("Cannot allocate memory", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("Resource temporarily unavailable", StringComparison.OrdinalIgnoreCase));

    internal static string SanitizeDecoderDiagnostic(string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return "FFmpeg rejected the file without a diagnostic.";
        }

        var sanitized = diagnostic.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 2000 ? sanitized : sanitized[..2000];
    }

    /// <summary>
    /// Builds a temp path that keeps the source's extension, since FFmpeg uses it as a demuxer
    /// hint. An unrecognised extension becomes .bin rather than being trusted.
    /// </summary>
    internal static string BuildTempPath(string extension, string prefix)
    {
        var normalized = (extension ?? string.Empty).ToLowerInvariant();
        if (!MusicFileExtensions.ValidAudioExtensions.Contains(normalized))
        {
            normalized = ".bin";
        }

        return Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}{normalized}");
    }

    private static string QuoteProcessArgument(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;

    private static void TryKill(Process process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort only.
        }
    }
}
