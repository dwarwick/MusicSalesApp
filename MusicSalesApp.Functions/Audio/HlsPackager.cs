using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Functions.Audio;

/// <summary>
/// Turns one audio file into an AES-128 encrypted HLS package on local disk.
///
/// <para>
/// Separate from <see cref="FfmpegAudioProcessor"/> because it is the one FFmpeg invocation that
/// cannot go through FFMpegCore: <c>-hls_key_info_file</c> has no binding there, and the output is a
/// <em>directory</em> of files rather than the single destination path every other call produces.
/// Both differences ripple — temp cleanup becomes a recursive directory delete, and the result has
/// to be parsed back out of the manifest rather than measured from one file.
/// </para>
/// </summary>
public interface IHlsPackager
{
    /// <summary>
    /// Segments and encrypts <paramref name="sourcePath"/> into a fresh temporary directory.
    ///
    /// <para>
    /// The caller owns the returned directory <b>only on a playable result</b>, and must then delete
    /// it recursively — it holds the plaintext key file as well as the segments. Every other
    /// outcome deletes it here, because a failure result deliberately carries no
    /// <c>OutputDirectory</c>: there would be nothing for the caller to delete it by, and the
    /// earlier contract that asked them to do it anyway left a content key on the worker after
    /// every failure.
    /// </para>
    /// </summary>
    /// <param name="contentKey">The 16 raw bytes to encrypt with.</param>
    /// <param name="iv">The 16-byte initialisation vector.</param>
    Task<HlsPackageResult> PackageAsync(
        string sourcePath,
        byte[] contentKey,
        byte[] iv,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class HlsPackager : IHlsPackager
{
    /// <summary>
    /// Segment length. Six seconds rather than the four the original design suggested: segment count
    /// is what drives request count, and since segments are served straight from storage the only
    /// thing shorter segments buy is faster seeking, which for music is not worth 50% more blobs.
    /// </summary>
    private const int SegmentSeconds = 6;

    /// <summary>
    /// AAC at 192 kbit/s, matching the MP3 bitrate the pipeline already produces.
    ///
    /// <para>
    /// AAC rather than passing the MP3 through, because MP3-in-MPEG-TS is poorly supported by
    /// Safari and by hls.js, and the whole point of this format choice is that it plays everywhere.
    /// The re-encode costs one generation — mitigated by the caller packaging from the creator's
    /// retained original whenever one exists that is not itself the playback MP3.
    /// </para>
    /// </summary>
    private const int AudioBitrateKbps = 192;

    /// <summary>Matches the host's own 10-minute ceiling; anything longer was never going to finish.</summary>
    private static readonly TimeSpan PackagingTimeout = TimeSpan.FromMinutes(10);

    private readonly ILogger<HlsPackager> _logger;

    public HlsPackager(ILogger<HlsPackager> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HlsPackageResult> PackageAsync(
        string sourcePath,
        byte[] contentKey,
        byte[] iv,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentKey);
        ArgumentNullException.ThrowIfNull(iv);

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return HlsPackageResult.Unplayable("MissingInput", "The audio file does not exist.");
        }

        var ffmpegPath = FfmpegAudioProcessor.ResolveFfmpegExecutablePath();
        if (ffmpegPath is null)
        {
            return HlsPackageResult.Inconclusive(
                "FfmpegUnavailable",
                "FFmpeg is not installed or could not be located on this worker.");
        }

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"hls-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex)
        {
            return HlsPackageResult.Inconclusive("LocalIoFailure", Sanitize(ex.Message));
        }

        HlsPackageResult result;
        try
        {
            result = await PackageIntoAsync(ffmpegPath, sourcePath, outputDirectory, contentKey, iv, cancellationToken);
        }
        catch
        {
            // Covers the cancellation rethrow too. Anything leaving by exception still has to take
            // the key file with it.
            TryDeleteDirectory(outputDirectory);
            throw;
        }

        // The directory is handed over only on success, so this is the one place the failure paths
        // can be swept - and they are the paths that most need it. Each one leaves a plaintext
        // content.key behind on a worker whose disk a later execution reuses.
        if (result.Status != AudioDecodeStatus.Playable)
        {
            TryDeleteDirectory(outputDirectory);
        }

        return result;
    }

    /// <summary>
    /// The packaging itself. Leaves <paramref name="outputDirectory"/> in place whatever happens, so
    /// that its one caller decides what to keep.
    /// </summary>
    private async Task<HlsPackageResult> PackageIntoAsync(
        string ffmpegPath,
        string sourcePath,
        string outputDirectory,
        byte[] contentKey,
        byte[] iv,
        CancellationToken cancellationToken)
    {
        try
        {
            var keyInfoPath = await WriteKeyInfoFileAsync(outputDirectory, contentKey, iv, cancellationToken);
            var manifestPath = Path.Combine(outputDirectory, HlsPackagePaths.ManifestFileName);

            var result = await RunFfmpegAsync(
                ffmpegPath,
                sourcePath,
                keyInfoPath,
                outputDirectory,
                manifestPath,
                cancellationToken);

            if (result != null)
            {
                return result;
            }

            return ReadPackage(outputDirectory, manifestPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException ex)
        {
            return HlsPackageResult.Inconclusive("LocalIoFailure", Sanitize(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return HlsPackageResult.Inconclusive("LocalAccessFailure", Sanitize(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to package {Path} as encrypted HLS", Path.GetFileName(sourcePath));
            return HlsPackageResult.Inconclusive("PackagerInfrastructureFailure", Sanitize(ex.Message));
        }
    }

    /// <summary>
    /// Writes FFmpeg's three-line key info file, and the raw key file beside it.
    ///
    /// <para>
    /// Line 1 is what FFmpeg writes into the manifest's <c>#EXT-X-KEY</c> URI. It is a
    /// <b>placeholder</b>, never a real URL: the real one carries a token that lives about a minute,
    /// and baking that into a stored manifest would both persist a credential and pin the manifest
    /// to one listener at one moment. The API substitutes it per request.
    /// </para>
    ///
    /// <para>
    /// Line 2 is the local path FFmpeg reads the key bytes from, and line 3 the IV. The key file is
    /// plaintext on the worker's disk for the length of one execution, which is why the caller
    /// deletes this whole directory recursively rather than picking files out of it.
    /// </para>
    /// </summary>
    private static async Task<string> WriteKeyInfoFileAsync(
        string outputDirectory,
        byte[] contentKey,
        byte[] iv,
        CancellationToken cancellationToken)
    {
        var keyFilePath = Path.Combine(outputDirectory, "content.key");
        await File.WriteAllBytesAsync(keyFilePath, contentKey, cancellationToken);

        var keyInfoPath = Path.Combine(outputDirectory, "enc.keyinfo");
        var lines = new[]
        {
            HlsPackagePaths.KeyUriPlaceholder,
            keyFilePath,
            Convert.ToHexString(iv).ToLowerInvariant()
        };

        await File.WriteAllLinesAsync(keyInfoPath, lines, cancellationToken);
        return keyInfoPath;
    }

    private async Task<HlsPackageResult> RunFfmpegAsync(
        string ffmpegPath,
        string sourcePath,
        string keyInfoPath,
        string outputDirectory,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var segmentPattern = Path.Combine(outputDirectory, HlsPackagePaths.SegmentFilePattern);

        var arguments =
            "-hide_banner -nostdin -y "
            + $"-i {Quote(sourcePath)} "
            + $"-c:a aac -b:a {AudioBitrateKbps}k -vn "
            + $"-hls_time {SegmentSeconds.ToString(CultureInfo.InvariantCulture)} "
            + "-hls_playlist_type vod "
            + "-hls_list_size 0 "
            + $"-hls_key_info_file {Quote(keyInfoPath)} "
            + $"-hls_segment_filename {Quote(segmentPattern)} "
            + Quote(manifestPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PackagingTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return HlsPackageResult.Inconclusive(
                "PackagerTimeout",
                $"FFmpeg did not finish packaging within {PackagingTimeout.TotalMinutes:0} minutes.");
        }

        var standardError = await standardErrorTask;
        await standardOutputTask;

        if (process.ExitCode == 0)
        {
            return null;
        }

        // The same distinction the decode path makes, and for the same reason: a worker that ran out
        // of disk must not condemn the file.
        var diagnostic = Sanitize(standardError);
        return FfmpegAudioProcessor.IsInfrastructureDiagnostic(standardError)
            ? HlsPackageResult.Inconclusive("PackagerInfrastructureFailure", diagnostic)
            : HlsPackageResult.Unplayable(MediaProcessingFailureCodes.PackagingFailed, diagnostic);
    }

    /// <summary>
    /// Reads the manifest FFmpeg produced and confirms it actually describes something.
    ///
    /// <para>
    /// A zero-exit run that produced no segments is treated as a failure of the file rather than a
    /// success, because publishing a package with an empty manifest would give the catalogue a song
    /// that plays silence — and would do it silently, since nothing downstream re-checks.
    /// </para>
    /// </summary>
    private static HlsPackageResult ReadPackage(string outputDirectory, string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return HlsPackageResult.Unplayable(
                MediaProcessingFailureCodes.PackagingProducedNothing,
                "FFmpeg reported success but wrote no manifest.");
        }

        var segments = new List<string>();
        var totalDuration = 0d;
        var targetDuration = 0d;

        foreach (var rawLine in File.ReadLines(manifestPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                if (double.TryParse(
                        line["#EXT-X-TARGETDURATION:".Length..],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    targetDuration = parsed;
                }

                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..];
                var comma = value.IndexOf(',');
                if (comma >= 0)
                {
                    value = value[..comma];
                }

                if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    totalDuration += seconds;
                }

                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            // Only names this class expects are accepted, so nothing FFmpeg happened to leave in the
            // directory can be published to listeners by being mentioned in a manifest.
            if (!HlsPackagePaths.IsSegmentFileName(line))
            {
                return HlsPackageResult.Unplayable(
                    MediaProcessingFailureCodes.PackagingProducedNothing,
                    $"The manifest referenced an unexpected file '{line}'.");
            }

            if (!File.Exists(Path.Combine(outputDirectory, line)))
            {
                return HlsPackageResult.Unplayable(
                    MediaProcessingFailureCodes.PackagingProducedNothing,
                    $"The manifest referenced '{line}', which FFmpeg did not write.");
            }

            segments.Add(line);
        }

        if (segments.Count == 0)
        {
            return HlsPackageResult.Unplayable(
                MediaProcessingFailureCodes.PackagingProducedNothing,
                "FFmpeg produced a manifest listing no segments.");
        }

        return HlsPackageResult.Packaged(
            outputDirectory,
            segments,
            targetDuration > 0 ? targetDuration : SegmentSeconds,
            totalDuration > 0 ? totalDuration : null);
    }

    /// <summary>Generates a fresh AES-128 content key and IV.</summary>
    public static (byte[] Key, byte[] Iv) CreateKeyMaterial()
        => (RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16));

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static string Sanitize(string diagnostic)
        => FfmpegAudioProcessor.SanitizeDecoderDiagnostic(diagnostic);

    /// <summary>
    /// Removes the packaging directory recursively. Recursive because this is the one FFmpeg call
    /// that writes a directory rather than a file, and that directory holds the plaintext key.
    /// </summary>
    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Never worth failing the execution over: the host reclaims the instance's disk. Logged
            // as a warning because a recurring failure here means key material is accumulating.
            _logger.LogWarning(ex, "Could not delete the HLS packaging directory {Directory}", directory);
        }
    }

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
            // Nothing useful to do: the execution is already failing and the host will reclaim the
            // process. Matches how FfmpegAudioProcessor treats the same situation.
        }
    }
}
