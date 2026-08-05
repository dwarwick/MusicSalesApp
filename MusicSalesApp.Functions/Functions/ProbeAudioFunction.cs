using Microsoft.Azure.Functions.Worker;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions.Audio;
using MusicSalesApp.Functions.Services;

namespace MusicSalesApp.Functions.Functions;

/// <summary>
/// Decodes an already-stored playback blob without producing anything.
///
/// <para>
/// Serves the media-integrity audit, which re-decodes every song in the catalogue, and the nightly
/// track-length repair. Both used to run this decode on the web server; the audit in particular
/// meant pulling every playback blob down to shared hosting and decoding it there.
/// </para>
///
/// <para>
/// No progress reporting: nobody is watching these, and an audit over a large catalogue would
/// generate an enormous number of updates for nothing.
/// </para>
///
/// <para>
/// This reports facts only - blob properties, detected container, duration, and a three-way
/// verdict. Classifying a song as healthy, repairable or quarantine-worthy stays in the web app so
/// those rules can change without redeploying this.
/// </para>
/// </summary>
public sealed class ProbeAudioFunction
{
    private readonly IFfmpegAudioProcessor _ffmpeg;
    private readonly IMediaBlobStore _blobStore;
    private readonly IMediaProcessingCallbackClient _callbackClient;
    private readonly ILogger<ProbeAudioFunction> _logger;

    public ProbeAudioFunction(
        IFfmpegAudioProcessor ffmpeg,
        IMediaBlobStore blobStore,
        IMediaProcessingCallbackClient callbackClient,
        ILogger<ProbeAudioFunction> logger)
    {
        _ffmpeg = ffmpeg;
        _blobStore = blobStore;
        _callbackClient = callbackClient;
        _logger = logger;
    }

    [Function(nameof(ProbeAudio))]
    public async Task ProbeAudio(
        [QueueTrigger("%MediaProcessing:ProbeQueueName%", Connection = "StagingStorageConnectionString")]
        AudioProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = new AudioProbeResult
        {
            ProbeId = request.ProbeId,
            Kind = request.Kind,
            AuditRunId = request.AuditRunId,
            SongMetadataId = request.SongMetadataId,
            BlobPath = request.BlobPath,
            Attempt = request.Attempt
        };

        var localPath = FfmpegAudioProcessor.BuildTempPath(".mp3", "media-probe");

        try
        {
            var properties = await _blobStore.GetMediaPropertiesAsync(request.BlobPath, cancellationToken);
            result.BlobExists = properties.Exists;
            result.BlobLength = properties.Length;
            result.ETag = properties.ETag;
            result.ContentType = properties.ContentType;
            result.BlobLastModified = properties.LastModified;

            if (!properties.Exists)
            {
                result.Outcome = AudioProcessingOutcome.Unplayable;
                result.FailureCode = "PlaybackBlobMissing";
                result.Diagnostic = "The registered playback blob does not exist.";
                await _callbackClient.PostProbeResultAsync(result, cancellationToken);
                return;
            }

            if (!await _blobStore.TryDownloadMediaAsync(request.BlobPath, localPath, cancellationToken))
            {
                // Deleted between the properties read and the download.
                result.BlobExists = false;
                result.Outcome = AudioProcessingOutcome.Unplayable;
                result.FailureCode = "PlaybackBlobMissing";
                result.Diagnostic = "The registered playback blob disappeared while it was being read.";
                await _callbackClient.PostProbeResultAsync(result, cancellationToken);
                return;
            }

            var downloadedLength = new FileInfo(localPath).Length;
            if (properties.Length.HasValue && downloadedLength != properties.Length.Value)
            {
                // A short read is evidence about the blob, not about this worker - the audit needs
                // to see it rather than have it retried away.
                result.Outcome = AudioProcessingOutcome.Unplayable;
                result.FailureCode = "LengthMismatch";
                result.Diagnostic =
                    $"Azure reported {properties.Length.Value} bytes but {downloadedLength} bytes were read.";
                await _callbackClient.PostProbeResultAsync(result, cancellationToken);
                return;
            }

            await using (var stream = File.OpenRead(localPath))
            {
                AudioContainerSniffer.ContentMatchesExtension(stream, request.BlobPath, out var detected);
                result.DetectedFormat = detected;
            }

            var decode = await _ffmpeg.DecodeAsync(localPath, cancellationToken);
            result.DurationSeconds = decode.Duration;
            result.Outcome = decode.ToOutcome();
            result.FailureCode = decode.FailureCode;
            result.Diagnostic = decode.Diagnostic;

            await _callbackClient.PostProbeResultAsync(result, cancellationToken);

            _logger.LogInformation(
                "Probed song {SongId} (attempt {Attempt}): {Outcome}",
                request.SongMetadataId,
                request.Attempt,
                result.Outcome);
        }
        finally
        {
            TempFileHelper.TryDelete(localPath, _logger);
        }
    }
}
