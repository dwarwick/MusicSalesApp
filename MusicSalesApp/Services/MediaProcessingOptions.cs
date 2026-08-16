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
    /// How long a lyrics-alignment attempt may sit without its step advancing before the reconciler
    /// looks into it.
    ///
    /// <para>
    /// <b>Shorter than <see cref="StalledJobTimeout"/>, not longer, and the inversion is the point.</b>
    /// That one is generous because when it fires it can only guess, so guessing early is dangerous.
    /// This one is the trigger for a <em>question</em>: lyrics alignment runs as a Durable
    /// orchestration started over HTTP, so its instance id is recorded, and the reconciler asks Azure
    /// what actually happened rather than inferring death from a stale timestamp. Asking early costs
    /// nothing and answers three cases the audio pipeline cannot distinguish at all - failed,
    /// cancelled, and finished-but-the-callback-was-lost.
    /// </para>
    ///
    /// <para>
    /// It still has to clear the worst case honestly, because a run that is merely slow must not be
    /// interrupted: roughly fifty minutes of orchestration (bounded by the Function app's own
    /// <c>functionTimeout</c>), plus the orchestrator's failure-reporting retry window, plus the
    /// terminal callback's own timeout - a little over an hour. Ninety minutes leaves headroom for a
    /// cold start without letting a genuinely dead attempt sit unresolved all day.
    /// </para>
    /// </summary>
    public TimeSpan LyricsStalledJobTimeout { get; set; } = TimeSpan.FromMinutes(90);

    /// <summary>
    /// How long a lyrics attempt the reconciler cannot get an answer about is left alone before it
    /// is written off.
    ///
    /// <para>
    /// Separate from <see cref="LyricsStalledJobTimeout"/> because it covers a different situation.
    /// "Azure says it failed" is a verdict and acts immediately; "we could not ask Azure" is not a
    /// verdict at all, and treating it as one would fail healthy runs during an outage - exactly the
    /// bug the audio pipeline's history is a record of. So an unanswerable attempt is retried on
    /// every sweep and only written off once it has been unanswerable for far longer than any run
    /// could legitimately take.
    /// </para>
    /// </summary>
    public TimeSpan LyricsUnreachableJobTimeout { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Lifetime of the read SAS minted over the staging container for a cross-account copy. Short,
    /// because it is created per assembly and used immediately.
    /// </summary>
    public TimeSpan StagingSasLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Lifetime of a write SAS handed to the browser for one direct upload.
    ///
    /// <para>
    /// Minted per file immediately before that file starts, not per batch - a 24-file batch spans
    /// the better part of an hour, and a per-batch token would force a multi-hour window onto files
    /// that may never be uploaded at all. Thirty minutes comfortably covers one large file on a slow
    /// connection, and a browser that outruns it asks for a fresh token through
    /// <c>UploadFilesModel.RenewUploadTarget</c> rather than losing the transfer - which matters,
    /// because the admin audio cap defaults to 150 MB and half an hour of that is only ~700 kbit/s.
    /// </para>
    /// </summary>
    public TimeSpan StagingUploadSasLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far back a write SAS starts, absorbing clock skew between this host and Azure.
    ///
    /// <para>
    /// The four read-SAS sites set no start time and are fine, because Azure itself consumes them or
    /// a browser reads them seconds later. These are minted on shared hosting whose clock we do not
    /// control and evaluated against the storage service's, where a few seconds fast means an
    /// immediate 403 the creator can do nothing about.
    /// </para>
    /// </summary>
    public TimeSpan StagingUploadSasClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>True when enough is configured for the pipeline to run at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(StorageConnectionString)
        && !string.IsNullOrWhiteSpace(StagingContainerName);
}
