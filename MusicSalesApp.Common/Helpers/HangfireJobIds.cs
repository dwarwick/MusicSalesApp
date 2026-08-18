namespace MusicSalesApp.Common.Helpers;

public static class HangfireJobIds
{
    public const string RotateFeaturedSongs = "rotate-featured-songs";
    public const string StorageBackup = "storage-backup";
    public const string CleanupStalePayPalCheckouts = "cleanup-stale-paypal-checkouts";
    public const string ReobservePayPalMismatches = "reobserve-paypal-subscription-mismatches";

    /// <summary>
    /// Fails upload jobs and closes audit runs whose Azure Function callbacks never arrived.
    /// Without it a poisoned queue message leaves a creator watching a progress bar forever.
    /// </summary>
    public const string ReconcileStalledMediaProcessing = "reconcile-stalled-media-processing";

    /// <summary>
    /// Deletes temp files left behind when a process died mid-upload. Nothing else sweeps a service
    /// account's temp directory on Windows, so without this an orphan is permanent.
    /// </summary>
    public const string CleanupOrphanedTempFiles = "cleanup-orphaned-temp-files";

    /// <summary>
    /// Resolves lyrics-alignment attempts that stopped reporting, by asking Azure what became of the
    /// orchestration behind each one.
    ///
    /// <para>
    /// A Durable orchestration has no poison queue - its trigger message is deleted the moment the
    /// run is scheduled - so a failure hours later produces no platform event at all. This is the
    /// only detector besides the orchestrator's own error handling, and the only thing that can
    /// finish a run which succeeded but whose callback was lost.
    /// </para>
    /// </summary>
    public const string ReconcileStalledLyricsAlignment = "reconcile-stalled-lyrics-alignment";
}
