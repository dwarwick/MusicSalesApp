using Microsoft.Azure.Functions.Worker;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Functions.Matching;
using MusicSalesApp.Functions.Services;

namespace MusicSalesApp.Functions.Functions;

/// <summary>
/// Pairs a batch of staged cover art with the audio files a creator dropped alongside it.
///
/// <para>
/// This is the phase that used to run on the Blazor circuit behind a static "Matching cover art..."
/// message: several vision calls, serially, holding image byte arrays in memory on a shared-hosting
/// web server, with no progress and no timeout. The creator's page is still waiting on the answer,
/// but it is waiting on a SignalR message with a deadline rather than on a blocked circuit — and if
/// this never answers, the page matches on filenames itself and the upload proceeds regardless.
/// </para>
///
/// <para>
/// Unlike the song pipeline there is no job row behind any of this. The batch exists only for as
/// long as the creator's page is waiting, its staged images are swept by the container's lifecycle
/// rule, and a lost message costs a worse pairing rather than a lost song.
/// </para>
/// </summary>
public sealed class MatchCoverArtFunction
{
    private readonly IMediaBlobStore _blobStore;
    private readonly IOpenAiFileMatcher _matcher;
    private readonly IMediaProcessingCallbackClient _callbackClient;
    private readonly ILogger<MatchCoverArtFunction> _logger;

    public MatchCoverArtFunction(
        IMediaBlobStore blobStore,
        IOpenAiFileMatcher matcher,
        IMediaProcessingCallbackClient callbackClient,
        ILogger<MatchCoverArtFunction> logger)
    {
        _blobStore = blobStore;
        _matcher = matcher;
        _callbackClient = callbackClient;
        _logger = logger;
    }

    [Function(nameof(MatchCoverArt))]
    public async Task MatchCoverArt(
        [QueueTrigger("%MediaProcessing:MatchQueueName%", Connection = "StagingStorageConnectionString")]
        CoverArtMatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var imageCount = request.Images?.Count ?? 0;
        _logger.LogInformation(
            "Matching cover art for batch {BatchId}: {AudioCount} audio file(s), {ImageCount} image(s)",
            request.BatchId,
            request.AudioFileNames?.Count ?? 0,
            imageCount);

        var progress = new CoverArtMatchProgressReporter(
            _callbackClient, request.BatchId, request.CreatorId, imageCount);

        CoverArtMatchResult result;
        try
        {
            if (!_matcher.IsConfigured)
            {
                // Not an error. An environment with no key configured gets deterministic base-name
                // matching, which is what the site did before any of this existed.
                _logger.LogInformation(
                    "OpenAI is not configured; batch {BatchId} will match on filenames alone.", request.BatchId);
                result = IOpenAiFileMatcher.FallbackMatch(request);
            }
            else
            {
                await progress.ReportStepAsync(CoverArtMatchStep.Reading, cancellationToken);
                await progress.ReportStepAsync(CoverArtMatchStep.ReadingText, cancellationToken);

                result = await _matcher.MatchAsync(
                    request,
                    (candidate, token) => _blobStore.TryReadStagedAsync(candidate.BlobPath, token),
                    read => _ = progress.ReportImagesReadAsync(read, cancellationToken),
                    cancellationToken);

                await progress.ReportStepAsync(CoverArtMatchStep.Pairing, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Degrade rather than retry. The creator is watching a bar with a deadline on it, and a
            // second attempt at a model call that just failed would more likely burn that deadline
            // than beat it - at which point the page falls back to exactly this answer anyway,
            // several minutes later.
            _logger.LogWarning(
                ex, "Cover-art matching failed for batch {BatchId}; falling back to filename matching", request.BatchId);
            result = IOpenAiFileMatcher.FallbackMatch(request);
            result.Diagnostic = "Matching fell back to filenames.";
        }

        // Throws on a non-2xx so the queue redelivers: the page is blocked on this message, and
        // losing it to a transient blip would cost the batch its pairing.
        await _callbackClient.PostMatchResultAsync(result, cancellationToken);
    }
}
