using Microsoft.Azure.Functions.Worker;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions.Audio;
using MusicSalesApp.Functions.Images;
using MusicSalesApp.Functions.Services;

namespace MusicSalesApp.Functions.Functions;

/// <summary>
/// Turns one staged creator upload into a playback MP3.
///
/// <para>
/// This is the work that used to freeze the Blazor upload page: a WAV cost three full FFmpeg passes
/// on the request thread before a byte reached Azure. Here each song gets its own instance -
/// host.json pins <c>batchSize: 1</c> so target-based scaling adds an instance per queued message
/// rather than piling concurrent transcodes onto one small VM.
/// </para>
/// </summary>
public sealed class ProcessAudioUploadFunction
{
    private readonly IFfmpegAudioProcessor _ffmpeg;
    private readonly IMediaBlobStore _blobStore;
    private readonly ICoverArtRenditionGenerator _renditionGenerator;
    private readonly IMediaProcessingCallbackClient _callbackClient;
    private readonly ILogger<ProcessAudioUploadFunction> _logger;

    public ProcessAudioUploadFunction(
        IFfmpegAudioProcessor ffmpeg,
        IMediaBlobStore blobStore,
        ICoverArtRenditionGenerator renditionGenerator,
        IMediaProcessingCallbackClient callbackClient,
        ILogger<ProcessAudioUploadFunction> logger)
    {
        _ffmpeg = ffmpeg;
        _blobStore = blobStore;
        _renditionGenerator = renditionGenerator;
        _callbackClient = callbackClient;
        _logger = logger;
    }

    [Function(nameof(ProcessAudioUpload))]
    public async Task ProcessAudioUpload(
        [QueueTrigger("%MediaProcessing:TranscodeQueueName%", Connection = "StagingStorageConnectionString")]
        AudioTranscodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var progress = new ProgressReporter(_callbackClient, request.JobId);
        var sourcePath = FfmpegAudioProcessor.BuildTempPath(request.SourceExtension, "upload-source");
        var playbackPath = FfmpegAudioProcessor.BuildTempPath(".mp3", "upload-playback");
        // Not FfmpegAudioProcessor.BuildTempPath: that whitelists against audio extensions, so a
        // .jpg would come back as .bin. The extension is cosmetic here anyway - SkiaSharp decodes by
        // content - but a scratch file named for what it holds is worth the two lines.
        var coverPath = Path.Combine(
            Path.GetTempPath(),
            $"upload-cover-{Guid.NewGuid():N}{NormalizeCoverExtension(request.CoverArtExtension)}");

        _logger.LogInformation(
            "Processing upload {JobId} ({FileName})",
            request.JobId,
            request.SourceFileName);

        try
        {
            await progress.ReportStepAsync(AudioProcessingStep.Downloading, cancellationToken: cancellationToken);

            try
            {
                await _blobStore.DownloadStagedAsync(request.SourceBlobPath, sourcePath, cancellationToken);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Nothing to retry: the staged blob is gone, so this message can never succeed.
                // Report it as a terminal failure rather than letting it cycle to the poison queue.
                await ReportFailureAsync(
                    request,
                    MediaProcessingFailureCodes.SourceBlobMissing,
                    "The uploaded file could not be found. Please try uploading it again.",
                    cancellationToken);
                return;
            }

            await progress.ReportStepAsync(AudioProcessingStep.Analyzing, cancellationToken: cancellationToken);

            // The source is decoded before anything else for two reasons: it proves the upload is
            // playable at all, and its duration is what turns the transcode's progress callback
            // into a real percentage instead of a guess.
            var sourceDecode = await _ffmpeg.DecodeAsync(sourcePath, cancellationToken);
            if (sourceDecode.Status == AudioDecodeStatus.Inconclusive)
            {
                // The decoder could not run - a full disk, no memory, a missing binary. The file is
                // probably fine, so throw and let the queue try again on another instance.
                throw new InvalidOperationException(
                    $"FFmpeg could not decode job {request.JobId}: "
                    + $"{sourceDecode.FailureCode} {sourceDecode.Diagnostic}");
            }

            if (sourceDecode.Status != AudioDecodeStatus.Playable)
            {
                await ReportFailureAsync(
                    request,
                    MediaProcessingFailureCodes.NotDecodable,
                    sourceDecode.Diagnostic ?? "This file could not be decoded as playable audio.",
                    cancellationToken);
                return;
            }

            var sourceIsMp3 = string.Equals(request.SourceExtension, ".mp3", StringComparison.OrdinalIgnoreCase);
            var duration = sourceDecode.Duration;

            await progress.ReportStepAsync(AudioProcessingStep.Transcoding, cancellationToken: cancellationToken);

            // An MP3 upload is already the playback copy: the site stores it once rather than
            // keeping an identical "original" beside it, so there is nothing to produce. The step is
            // still reported above so the creator's bar advances the same way either way.
            if (!sourceIsMp3)
            {
                await _ffmpeg.TranscodeToMp3Async(
                    sourcePath,
                    playbackPath,
                    sourceDecode.Duration,
                    new Progress<double>(percent => _ = progress.ReportStepPercentAsync(
                        AudioProcessingStep.Transcoding,
                        percent,
                        cancellationToken)),
                    cancellationToken);

                await progress.ReportStepAsync(AudioProcessingStep.Verifying, cancellationToken: cancellationToken);

                // Cheap container sniff first, so a truncated transcode is caught before a full
                // second decode is spent on it.
                await using (var produced = File.OpenRead(playbackPath))
                {
                    if (!AudioContainerSniffer.ContentMatchesExtension(produced, ".mp3", out _))
                    {
                        await ReportFailureAsync(
                            request,
                            MediaProcessingFailureCodes.InvalidMp3Container,
                            "FFmpeg did not produce a valid MP3 container.",
                            cancellationToken);
                        return;
                    }
                }

                var playbackDecode = await _ffmpeg.DecodeAsync(playbackPath, cancellationToken);
                if (playbackDecode.Status == AudioDecodeStatus.Inconclusive)
                {
                    throw new InvalidOperationException(
                        $"FFmpeg could not verify the transcode for job {request.JobId}: "
                        + $"{playbackDecode.FailureCode} {playbackDecode.Diagnostic}");
                }

                if (playbackDecode.Status != AudioDecodeStatus.Playable
                    || !playbackDecode.Duration.HasValue
                    || playbackDecode.Duration <= 0)
                {
                    await ReportFailureAsync(
                        request,
                        MediaProcessingFailureCodes.TranscodeFailed,
                        playbackDecode.Diagnostic ?? "The converted audio could not be decoded.",
                        cancellationToken);
                    return;
                }

                // The transcode's own measured length, not the source's - they can differ slightly,
                // and this is the file listeners will actually play.
                duration = playbackDecode.Duration;

                await progress.ReportStepAsync(AudioProcessingStep.Uploading, cancellationToken: cancellationToken);
                await _blobStore.UploadStagedAsync(
                    request.PlaybackBlobPath,
                    playbackPath,
                    "audio/mpeg",
                    cancellationToken);
            }

            if (!duration.HasValue || duration <= 0)
            {
                await ReportFailureAsync(
                    request,
                    MediaProcessingFailureCodes.ZeroDuration,
                    "The audio reported no playable duration.",
                    cancellationToken);
                return;
            }

            var renditions = await RenderCoverArtAsync(request, progress, coverPath, cancellationToken);

            await _callbackClient.PostTranscodeResultAsync(
                new AudioTranscodeResult
                {
                    JobId = request.JobId,
                    PlaybackBlobPath = sourceIsMp3 ? request.SourceBlobPath : request.PlaybackBlobPath,
                    DurationSeconds = duration,
                    Outcome = AudioProcessingOutcome.Playable,
                    SourceWasAlreadyMp3 = sourceIsMp3,
                    CoverArtVariantWidths = renditions?.Widths,
                    CoverArtWidth = renditions?.SourceWidth,
                    CoverArtHeight = renditions?.SourceHeight,
                    CoverArtDiagnosticCode = renditions?.DiagnosticCode
                },
                cancellationToken);
        }
        finally
        {
            // Unconditionally, and TryDelete no-ops on a path that was never written. A flag set
            // *after* the transcode returned missed the case that matters most: FFmpeg dying
            // part-way leaves a partial MP3 on disk, and the retry lands on the same instance with
            // only 500 MB of local storage to work in.
            TempFileHelper.TryDelete(sourcePath, _logger);
            TempFileHelper.TryDelete(playbackPath, _logger);
            TempFileHelper.TryDelete(coverPath, _logger);
        }
    }

    /// <summary>
    /// Keeps a creator-supplied extension out of the scratch filename unless it is one we recognise.
    /// </summary>
    private static string NormalizeCoverExtension(string extension)
    {
        var normalized = (extension ?? string.Empty).ToLowerInvariant();
        return MusicFileExtensions.ValidCoverArtExtensions.Contains(normalized) ? normalized : ".bin";
    }

    /// <summary>
    /// Builds the cover art's WebP renditions, or returns null when the upload has no cover art.
    ///
    /// <para>
    /// <b>Cannot fail the song.</b> Every outcome short of cancellation comes back as a result the
    /// caller reports alongside a <c>Playable</c> verdict — an empty width set simply means the song
    /// serves its full-size master, which is what every consumer did before renditions existed.
    /// There is deliberately no image equivalent of the audio path's <c>Inconclusive</c>-and-throw:
    /// retrying the message would re-run a transcode that already succeeded, costing minutes to
    /// salvage a thumbnail.
    /// </para>
    /// </summary>
    private async Task<CoverArtRenditionResult> RenderCoverArtAsync(
        AudioTranscodeRequest request,
        ProgressReporter progress,
        string coverPath,
        CancellationToken cancellationToken)
    {
        // Null rather than empty: "there was never any art" and "the art could not be rendered" are
        // different answers, and the API branches on the difference when it records the width set.
        if (string.IsNullOrWhiteSpace(request.CoverArtBlobPath))
            return null;

        await progress.ReportStepAsync(AudioProcessingStep.RenderingArtwork, cancellationToken: cancellationToken);

        var result = await _renditionGenerator.GenerateAsync(
            request.JobId,
            request.CoverArtBlobPath,
            request.CoverArtExtension,
            coverPath,
            new Progress<double>(percent => _ = progress.ReportStepPercentAsync(
                AudioProcessingStep.RenderingArtwork,
                percent,
                cancellationToken)),
            cancellationToken);

        if (result.Widths.Count == 0)
        {
            _logger.LogWarning(
                "No cover-art renditions were produced for job {JobId} ({DiagnosticCode}); "
                + "the song will serve its full-size master",
                request.JobId,
                result.DiagnosticCode);
        }
        else
        {
            _logger.LogInformation(
                "Wrote cover-art renditions {Widths} for job {JobId} from a {Width}x{Height} source",
                string.Join(',', result.Widths),
                request.JobId,
                result.SourceWidth,
                result.SourceHeight);
        }

        return result;
    }

    private async Task ReportFailureAsync(
        AudioTranscodeRequest request,
        string failureCode,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Upload {JobId} rejected: {FailureCode} {Diagnostic}",
            request.JobId,
            failureCode,
            diagnostic);

        await _callbackClient.PostTranscodeResultAsync(
            new AudioTranscodeResult
            {
                JobId = request.JobId,
                Outcome = AudioProcessingOutcome.Unplayable,
                FailureCode = failureCode,
                Diagnostic = diagnostic
            },
            cancellationToken);
    }
}
