using Microsoft.Azure.Functions.Worker;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions.Audio;
using MusicSalesApp.Functions.Services;

namespace MusicSalesApp.Functions.Functions;

/// <summary>
/// Packages one already-published song as AES-128 encrypted HLS.
///
/// <para>
/// Serves both the ordinary upload path — enqueued once a transcode has produced a playback MP3 —
/// and the one-time backfill that walks the existing catalogue. Both are the same work, so both go
/// through the same queue rather than the backfill needing its own worker.
/// </para>
///
/// <para>
/// A separate queue from the transcode, deliberately. <c>host.json</c> pins <c>batchSize: 1</c>, so
/// one instance handles one message; folding packaging into the transcode would put two full FFmpeg
/// passes under a single 10-minute Consumption ceiling and make a long song fail at the second one
/// having already succeeded at the first.
/// </para>
///
/// <para>
/// No progress reporting. For an upload the song is already published and playable through the MP3
/// by the time this runs, so nobody is blocked on it; for the backfill nobody is watching a
/// particular song.
/// </para>
/// </summary>
public sealed class PackageAudioFunction
{
    private readonly IHlsPackager _packager;
    private readonly IMediaBlobStore _blobStore;
    private readonly IMediaProcessingCallbackClient _callbackClient;
    private readonly ILogger<PackageAudioFunction> _logger;

    public PackageAudioFunction(
        IHlsPackager packager,
        IMediaBlobStore blobStore,
        IMediaProcessingCallbackClient callbackClient,
        ILogger<PackageAudioFunction> logger)
    {
        _packager = packager;
        _blobStore = blobStore;
        _callbackClient = callbackClient;
        _logger = logger;
    }

    [Function(nameof(PackageAudio))]
    public async Task PackageAudio(
        [QueueTrigger("%MediaProcessing:PackageQueueName%", Connection = "StagingStorageConnectionString")]
        AudioPackageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Measures this song only, so a backfill can be estimated from per-song cost rather than
        // from a run's wall clock - songs are packaged concurrently, so wall clock says more about
        // how many ran at once than about how long one takes.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = new AudioPackageResult
        {
            SongMetadataId = request.SongMetadataId,
            HlsStreamId = request.HlsStreamId,
            BackfillRunId = request.BackfillRunId
        };

        // The source keeps its real extension: FFmpeg uses it as a demuxer hint, and the backfill
        // may hand us a WAV or FLAC original rather than the playback MP3.
        var sourceExtension = Path.GetExtension(request.SourceBlobPath ?? string.Empty);
        var localPath = FfmpegAudioProcessor.BuildTempPath(sourceExtension, "hls-source");
        string outputDirectory = null;

        try
        {
            if (!await _blobStore.TryDownloadMediaAsync(request.SourceBlobPath, localPath, cancellationToken))
            {
                result.Outcome = AudioProcessingOutcome.Unplayable;
                result.FailureCode = MediaProcessingFailureCodes.SourceBlobMissing;
                result.Diagnostic = "The audio to package does not exist in the media container.";
                await _callbackClient.PostPackageResultAsync(result, cancellationToken);
                return;
            }

            var (contentKey, iv) = HlsPackager.CreateKeyMaterial();

            var package = await _packager.PackageAsync(localPath, contentKey, iv, cancellationToken);
            outputDirectory = package.OutputDirectory;

            if (package.Status == AudioDecodeStatus.Inconclusive)
            {
                // The worker could not do the work - out of disk, out of memory, binary missing.
                // Throwing returns the message to the queue so another instance tries, rather than
                // recording a healthy song as unpackageable. Same rule as the decode path, and the
                // reason AudioDecodeStatus is three-way at all.
                _logger.LogWarning(
                    "Packaging song {SongMetadataId} was inconclusive ({FailureCode}); returning it to the queue. {Diagnostic}",
                    request.SongMetadataId,
                    package.FailureCode,
                    package.Diagnostic);

                throw new InvalidOperationException(
                    $"Packaging song {request.SongMetadataId} could not run: {package.FailureCode}. {package.Diagnostic}");
            }

            if (package.Status == AudioDecodeStatus.Unplayable)
            {
                result.Outcome = AudioProcessingOutcome.Unplayable;
                result.FailureCode = package.FailureCode;
                result.Diagnostic = package.Diagnostic;
                await _callbackClient.PostPackageResultAsync(result, cancellationToken);
                return;
            }

            await UploadPackageAsync(request.HlsStreamId, package, cancellationToken);

            result.Outcome = AudioProcessingOutcome.Playable;
            result.KeyHex = Convert.ToHexString(contentKey).ToLowerInvariant();
            result.IvHex = Convert.ToHexString(iv).ToLowerInvariant();
            result.SegmentCount = package.SegmentFileNames.Count;
            result.TargetDurationSeconds = package.TargetDurationSeconds;
            result.DurationSeconds = package.DurationSeconds;
            result.ProcessingSeconds = stopwatch.Elapsed.TotalSeconds;

            await _callbackClient.PostPackageResultAsync(result, cancellationToken);

            _logger.LogInformation(
                "Packaged song {SongMetadataId} as {SegmentCount} encrypted segments under {StreamId} in {Seconds:0.0}s",
                request.SongMetadataId,
                result.SegmentCount,
                request.HlsStreamId,
                result.ProcessingSeconds);
        }
        finally
        {
            TempFileHelper.TryDelete(localPath, _logger);
            TryDeleteDirectory(outputDirectory);
        }
    }

    /// <summary>
    /// Uploads the manifest and every segment into the streaming container.
    ///
    /// <para>
    /// Segments go up <b>before</b> the manifest, so the folder is never in a state where a manifest
    /// names a segment that is not there yet. The manifest is what makes the package usable, so
    /// writing it last keeps a partially-uploaded package unusable rather than broken - and the
    /// callback that records the package only fires after all of this succeeds.
    /// </para>
    /// </summary>
    private async Task UploadPackageAsync(
        Guid hlsStreamId,
        HlsPackageResult package,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < package.SegmentFileNames.Count; index++)
        {
            var fileName = package.SegmentFileNames[index];
            var localSegment = Path.Combine(package.OutputDirectory, fileName);

            await using var stream = File.OpenRead(localSegment);
            await _blobStore.UploadStreamingAsync(
                $"{HlsPackagePaths.Folder(hlsStreamId)}/{fileName}",
                stream,
                HlsPackagePaths.SegmentContentType,
                cancellationToken);
        }

        var manifestPath = Path.Combine(package.OutputDirectory, HlsPackagePaths.ManifestFileName);
        await using var manifestStream = File.OpenRead(manifestPath);
        await _blobStore.UploadStreamingAsync(
            HlsPackagePaths.Manifest(hlsStreamId),
            manifestStream,
            HlsPackagePaths.ManifestContentType,
            cancellationToken);
    }

    /// <summary>
    /// Removes the packaging directory, recursively.
    ///
    /// <para>
    /// Recursive rather than the single-file <c>TempFileHelper.TryDelete</c> every other function
    /// uses, because this is the one FFmpeg call that produces a directory — and that directory
    /// holds the <b>plaintext content key</b> alongside the segments. Leaving it behind would leave
    /// key material on the worker's disk, and on a ~500 MB per-instance disk would eventually fail
    /// the next execution too.
    /// </para>
    /// </summary>
    private void TryDeleteDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Never fails the execution: the work is done and the host reclaims the instance's disk.
            // Logged as a warning because a recurring failure here means keys are accumulating.
            _logger.LogWarning(ex, "Could not delete the HLS packaging directory {Directory}", directory);
        }
    }
}
