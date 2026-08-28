using Microsoft.Azure.Functions.Worker;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions.Services;

namespace MusicSalesApp.Functions.Functions;

/// <summary>
/// Turns a packaging message that exhausted its retries into a reported failure.
///
/// <para>
/// The same arrangement as <see cref="HandleTranscodePoisonFunction"/>, and it exists for the same
/// reason: the alternative is a message that sits in a queue nobody reads while something else has
/// to <em>infer</em> that it died.
/// </para>
///
/// <para>
/// It matters more here than it looks. Packaging returns its message to the queue whenever the
/// worker itself could not run — that is what
/// <see cref="MusicSalesApp.Functions.Audio.HlsPackageResult"/>'s inconclusive status is for — so a
/// genuinely broken source can only be distinguished from a run of unlucky workers by the retries
/// running out. Without this, a song the backfill can never package would leave its run waiting for
/// a callback that never comes, and the run would never complete.
/// </para>
/// </summary>
public sealed class HandlePackagePoisonFunction
{
    private readonly IMediaProcessingCallbackClient _callbackClient;
    private readonly ILogger<HandlePackagePoisonFunction> _logger;

    public HandlePackagePoisonFunction(
        IMediaProcessingCallbackClient callbackClient,
        ILogger<HandlePackagePoisonFunction> logger)
    {
        _callbackClient = callbackClient;
        _logger = logger;
    }

    /// <remarks>
    /// The queue name is the package queue's with <c>-poison</c> appended, resolved textually the
    /// same way the transcode poison trigger is, so it needs no app setting of its own. Note the
    /// consequence for provisioning: the <em>live</em> queue still has to be created explicitly,
    /// because a queue trigger does not create a missing queue and a missing one stops the whole
    /// Function app from starting.
    /// </remarks>
    [Function(nameof(HandlePackagePoison))]
    public async Task HandlePackagePoison(
        [QueueTrigger("%MediaProcessing:PackageQueueName%-poison", Connection = "StagingStorageConnectionString")]
        AudioPackageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogError(
            "Packaging song {SongMetadataId} exhausted its retries and poisoned; reporting it failed.",
            request.SongMetadataId);

        await _callbackClient.PostPackageResultAsync(
            new AudioPackageResult
            {
                SongMetadataId = request.SongMetadataId,
                HlsStreamId = request.HlsStreamId,
                BackfillRunId = request.BackfillRunId,
                Outcome = AudioProcessingOutcome.Unplayable,
                FailureCode = MediaProcessingFailureCodes.PoisonedAfterRetries,
                Diagnostic = "This song could not be packaged for encrypted playback after several attempts."
            },
            cancellationToken);
    }
}
