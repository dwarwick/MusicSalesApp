using Microsoft.Azure.Functions.Worker;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions.Services;

namespace MusicSalesApp.Functions.Functions;

/// <summary>
/// Turns a transcode message that exhausted its retries into a reported failure.
///
/// <para>
/// The Functions host moves a message to <c>{queue}-poison</c> once it has been dequeued
/// <c>maxDequeueCount</c> times (3, in host.json). That is Azure stating, definitively, that this
/// upload will never be processed. Until this function existed nothing read those queues: the
/// message simply sat there, and <c>SongUploadJobReconciler</c> separately <em>inferred</em> the
/// same conclusion from a stale <c>StepUpdatedAt</c>.
/// </para>
///
/// <para>
/// That inference was the problem. <c>StepUpdatedAt</c> is refreshed only by progress pings, and
/// those swallow their own failures by design - so a web app restarting mid-batch looks exactly like
/// a dead Function, and the reconciler would fail songs that were processing perfectly well. Reacting
/// to the real event instead reports genuine failures sooner <em>and</em> more accurately, which is
/// what allows the reconciler's timeout to be relaxed to a length no deploy can trip.
/// </para>
///
/// <para>
/// Deliberately tiny. It posts through the same terminal callback every other outcome uses, so the
/// web app's <c>FailAsync</c> does the real work: mark the job, tell the creator, delete the staged
/// blobs, sweep any cover-art renditions the Function had already written into the media container.
/// </para>
/// </summary>
public sealed class HandleTranscodePoisonFunction
{
    private readonly IMediaProcessingCallbackClient _callbackClient;
    private readonly ILogger<HandleTranscodePoisonFunction> _logger;

    public HandleTranscodePoisonFunction(
        IMediaProcessingCallbackClient callbackClient,
        ILogger<HandleTranscodePoisonFunction> logger)
    {
        _callbackClient = callbackClient;
        _logger = logger;
    }

    /// <remarks>
    /// The queue name is the transcode queue's, with <c>-poison</c> appended. Binding expressions
    /// substitute <c>%setting%</c> textually, so this resolves to <c>audio-transcode{-env}-poison</c>
    /// without needing an app setting of its own - which is why this change needs no portal step.
    /// The host creates the queue on demand, and the message body is the original, unaltered, so it
    /// deserializes with the same binding the live queue uses.
    /// </remarks>
    [Function(nameof(HandleTranscodePoison))]
    public async Task HandleTranscodePoison(
        [QueueTrigger("%MediaProcessing:TranscodeQueueName%-poison", Connection = "StagingStorageConnectionString")]
        AudioTranscodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogError(
            "Transcode for job {JobId} ({FileName}) exhausted its retries and poisoned; reporting it failed.",
            request.JobId,
            request.SourceFileName);

        // Throws on a non-2xx, like every other terminal callback. A poison-queue trigger has its
        // own bounded retry, so this cannot loop - and giving up silently here would put the job
        // right back in the state this function exists to end: stuck, with nothing coming.
        await _callbackClient.PostTranscodeResultAsync(
            new AudioTranscodeResult
            {
                JobId = request.JobId,
                Outcome = AudioProcessingOutcome.Unplayable,
                FailureCode = MediaProcessingFailureCodes.PoisonedAfterRetries,
                Diagnostic = "This upload could not be processed after several attempts. "
                    + "Please try uploading it again."
            },
            cancellationToken);
    }
}
