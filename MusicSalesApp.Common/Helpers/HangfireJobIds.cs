namespace MusicSalesApp.Common.Helpers;

public static class HangfireJobIds
{
    public const string RotateFeaturedSongs = "rotate-featured-songs";
    public const string StorageBackup = "storage-backup";
    public const string CleanupStalePayPalCheckouts = "cleanup-stale-paypal-checkouts";
    public const string ReobservePayPalMismatches = "reobserve-paypal-subscription-mismatches";
}
