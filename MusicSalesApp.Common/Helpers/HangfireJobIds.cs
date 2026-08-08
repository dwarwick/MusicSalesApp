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
}
