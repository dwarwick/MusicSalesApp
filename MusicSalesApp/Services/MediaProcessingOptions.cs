using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Configuration for the audio-processing pipeline that runs in Azure Functions.
///
/// <para>
/// This addresses a <em>different storage account</em> from <see cref="AzureStorageOptions"/>. The
/// media account is Premium, and no Premium account type offers the Queue service, so the two
/// queues and the upload staging container live on the Standard general-purpose account. Song media
/// itself never moves.
/// </para>
///
/// <para>
/// Populated in <c>Program.cs</c> from the <b><c>AzureLowSpeed</c></b> section, which already holds
/// that account's connection string, plus the top-level <c>MediaProcessingApiKey</c>. There is
/// deliberately no second copy of the connection string: one section per storage account means one
/// value to rotate.
/// </para>
///
/// <para>
/// Consequence worth knowing before touching the assembly code: staging and media are on different
/// accounts, so copying a finished upload into its GUID folder is a cross-account copy needing a
/// source SAS, not a same-account server-side rename.
/// </para>
/// </summary>
public class MediaProcessingOptions
{
    /// <summary>
    /// Connection string for the Standard general-purpose account holding queues and staging.
    /// Comes from <c>AzureLowSpeed:StorageAccountConnectionString</c>.
    /// </summary>
    public string StorageConnectionString { get; set; }

    /// <summary>Staging container for this environment - musicuploads / -dev / -local.</summary>
    public string StagingContainerName { get; set; }

    /// <summary>Queue carrying transcode requests for creator uploads.</summary>
    public string TranscodeQueueName { get; set; } = MediaProcessingQueues.Transcode;

    /// <summary>Queue carrying decode-only probe requests for the maintenance jobs.</summary>
    public string ProbeQueueName { get; set; } = MediaProcessingQueues.Probe;

    /// <summary>Queue carrying cover-art pairing requests for the pre-upload matching phase.</summary>
    public string MatchQueueName { get; set; } = MediaProcessingQueues.CoverArtMatch;

    // The callback secret is deliberately NOT mirrored here. It was, and nothing read it: the only
    // consumer is RequireMediaProcessingApiKeyAttribute, which reads
    // AppSettingKeys.MediaProcessingApiKey from configuration. A second copy that agrees with the
    // real one until someone edits one of them is worse than no copy - the symptom would be the
    // Function's callbacks failing authorisation with both values looking correct in isolation.

    /// <summary>
    /// How long a job may sit without its step advancing before the reconciler declares it dead.
    ///
    /// <para>
    /// <b>Generous on purpose, because the reconciler is no longer what reports failures.</b> The
    /// poison-queue handler in the Function app does that, from the authoritative event, so this only
    /// has to catch what will never poison at all: a queue purged by hand, an enqueue that reported
    /// success but lost its message, or a Function App stopped long enough that messages sit
    /// un-dequeued.
    /// </para>
    ///
    /// <para>
    /// It was 20 minutes when it was the <em>only</em> detector, justified as clearing the 10-minute
    /// Consumption ceiling. That framing missed the real hazard: liveness here is refreshed only by
    /// progress pings, which swallow their own failures, so a web app restarting mid-batch is
    /// indistinguishable from a dead Function and every in-flight song got failed. This value must
    /// therefore also stay clear of the worst case time to poison
    /// (<c>maxDequeueCount</c> x <c>functionTimeout</c> = 3 x 10 min), or the reconciler can still
    /// beat the handler to a verdict and reintroduce exactly that bug.
    /// <c>PoisonHandlerBeatsTheReconcilerTests</c> pins the ordering.
    /// </para>
    /// </summary>
    public TimeSpan StalledJobTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Lifetime of the read SAS minted over the staging container for a cross-account copy. Short,
    /// because it is created per assembly and used immediately.
    /// </summary>
    public TimeSpan StagingSasLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>True when enough is configured for the pipeline to run at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(StorageConnectionString)
        && !string.IsNullOrWhiteSpace(StagingContainerName);
}
