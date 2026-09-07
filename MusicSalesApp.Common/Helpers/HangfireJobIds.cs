namespace MusicSalesApp.Common.Helpers;

public static class HangfireJobIds
{
    public const string RotateFeaturedSongs = "rotate-featured-songs";
    public const string StorageBackup = "storage-backup";
    public const string CleanupStalePayPalCheckouts = "cleanup-stale-paypal-checkouts";
    public const string ReobservePayPalMismatches = "reobserve-paypal-subscription-mismatches";

    /// <summary>
    /// Re-checks PayPal subscriptions that still grant access locally against the provider, so a
    /// lifecycle webhook that never arrived cannot leave a cancelled agreement entitling someone
    /// forever.
    ///
    /// <para>
    /// Nothing else covers this. NormalizeExpiredSubscriptions only touches rows with an EndDate in
    /// the past, ReobservePayPalMismatches only revisits episodes that are already open, and
    /// CleanupStalePayPalCheckouts is APPROVAL_PENDING only. Everything else that reconciles is
    /// triggered by the user visiting the site - which a lapsed subscriber has no reason to do.
    /// </para>
    /// </summary>
    public const string ReconcilePayPalEntitlementDrift = "reconcile-paypal-entitlement-drift";

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

    /// <summary>
    /// Turns newly-public songs into in-app release notifications for the artist's followers.
    /// Cheap and database-only, so it runs hourly and the notification shows up the same day
    /// rather than waiting for the overnight email pass.
    /// </summary>
    public const string CreateArtistReleaseNotifications = "create-artist-release-notifications";

    /// <summary>
    /// The slow SMTP half of the above. Separate because it sleeps between every message to stay
    /// out of spam filters, which is not something an hourly job can afford to do.
    /// </summary>
    public const string SendArtistReleaseNotificationEmails = "send-artist-release-notification-emails";

    /// <summary>
    /// Emails the thank-you messages creators have sent to individual followers. Runs every
    /// 15 minutes rather than nightly: a thank-you that lands 20 hours after the follow reads
    /// as broken rather than as a reply.
    /// </summary>
    public const string SendArtistMessageEmails = "send-artist-message-emails";

    /// <summary>
    /// Sends the push notifications due for release notifications and artist messages, and retires
    /// device tokens the platforms reject.
    ///
    /// <para>
    /// One job for both, because the expensive part is the outbound HTTP call per device rather
    /// than the query, and because a device is reached the same way whichever kind of notification
    /// it carries. Every 5 minutes - push has no spam-filter spacing to observe, unlike the email
    /// jobs, so it can afford to be the prompt channel.
    /// </para>
    /// </summary>
    public const string DispatchArtistPushNotifications = "dispatch-artist-push-notifications";
}
