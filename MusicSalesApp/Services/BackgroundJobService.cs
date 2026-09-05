using Hangfire;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing background jobs using Hangfire
/// </summary>
public class BackgroundJobService : IBackgroundJobService
{
    private readonly ILogger<BackgroundJobService> _logger;

    public BackgroundJobService(ILogger<BackgroundJobService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize all recurring Hangfire jobs
    /// </summary>
    public void InitializeRecurringJobs()
    {
        try
        {
            // Schedule weekly stream payouts every Monday at 1 AM UTC
            // This processes payouts to creators based on stream counts
            RecurringJob.AddOrUpdate<IStreamPayoutService>(
                "process-stream-payouts",
                service => service.ProcessPendingPayoutsAsync(),
                Cron.Weekly(DayOfWeek.Monday, 1));

            // Remove the old Supabase sync job id so that stale Hangfire records
            // from previous deployments don't keep firing after the method was removed.
            RecurringJob.RemoveIfExists("sync-likes-to-supabase");

            // Rotate home-page featured songs daily at midnight UTC.
            RecurringJob.AddOrUpdate<IFeaturedSongRotationService>(
                HangfireJobIds.RotateFeaturedSongs,
                service => service.RotateFeaturedSongsAsync(),
                Cron.Daily(0));

            // Schedule nightly display-order randomization before other catalog jobs.
            RecurringJob.AddOrUpdate<ISongDisplayOrderService>(
                "randomize-song-display-order",
                service => service.RandomizeDisplayOrderAsync(),
                "30 1 * * *");

            // Schedule nightly recommendation generation at 2 AM UTC
            RecurringJob.AddOrUpdate<IRecommendationService>(
                "generate-recommendations",
                service => service.GenerateAllRecommendationsAsync(),
                Cron.Daily(2));

            // Normalize stale subscription rows before other expiration-dependent jobs run.
            RecurringJob.AddOrUpdate<ISubscriptionService>(
                "normalize-expired-subscriptions",
                service => service.NormalizeExpiredSubscriptionsAsync(),
                "30 2 * * *");

            // Schedule daily cleanup job at 3 AM UTC
            RecurringJob.AddOrUpdate<IPlaylistCleanupService>(
                "cleanup-lapsed-subscription-playlists",
                service => service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync(),
                Cron.Daily(3));

            RecurringJob.AddOrUpdate<IUnverifiedUserCleanupService>(
                "cleanup-stale-unverified-users",
                service => service.DeleteStaleUnverifiedUsersAsync(),
                Cron.Daily(3, 15));

            // Verify abandoned PayPal checkouts against the provider and close them out, so stale
            // APPROVAL_PENDING rows stop accumulating as each user's "latest subscription".
            RecurringJob.AddOrUpdate<IPayPalCheckoutHygieneService>(
                HangfireJobIds.CleanupStalePayPalCheckouts,
                service => service.CleanupStalePendingCheckoutsAsync(),
                "45 3 * * *");

            // Backstop for the mismatch notification grace window: a persistent mismatch must still
            // be reported even if the affected user never polls the site again.
            RecurringJob.AddOrUpdate<IPayPalCheckoutHygieneService>(
                HangfireJobIds.ReobservePayPalMismatches,
                service => service.ReobserveOpenMismatchEpisodesAsync(),
                "20 * * * *");

            // Catch-up for missed PayPal lifecycle webhooks. 04:15 UTC sits clear of the other
            // daily jobs (03:45 checkout hygiene, 04:00 new-song emails) and of the hourly ones.
            RecurringJob.AddOrUpdate<IPayPalEntitlementDriftService>(
                HangfireJobIds.ReconcilePayPalEntitlementDrift,
                service => service.ReconcileDriftedSubscriptionsAsync(),
                "15 4 * * *");

            // Schedule nightly new song notification emails at 4 AM UTC
            // This runs after song cleanup and sends emails to opted-in users about new songs added in the past 24 hours
            RecurringJob.AddOrUpdate<INewSongNotificationService>(
                "send-new-song-notification-emails",
                service => service.SendNewSongNotificationsAsync(),
                Cron.Daily(4));

            RecurringJob.AddOrUpdate<IAdminMessageService>(
                "send-admin-message-emails",
                service => service.SendPendingEmailsAsync(),
                Cron.Daily(6));

            // Artist release notifications, split in two because the halves have opposite costs.
            //
            // Creating the rows is pure database work, so it runs hourly and a follower sees the
            // release the same day. Emailing them sleeps 5 seconds between messages to stay out of
            // spam filters, which only makes sense once a night. :40 is clear of the other hourly
            // jobs at :00 and :20.
            RecurringJob.AddOrUpdate<IArtistReleaseNotificationService>(
                HangfireJobIds.CreateArtistReleaseNotifications,
                service => service.CreatePendingNotificationsAsync(),
                "40 * * * *");

            // 04:30 UTC. 04:00 is the site-wide new-song digest and 04:15 the PayPal drift
            // reconcile, so this sits in the gap between them.
            RecurringJob.AddOrUpdate<IArtistReleaseNotificationService>(
                HangfireJobIds.SendArtistReleaseNotificationEmails,
                service => service.SendPendingEmailsAsync(),
                Cron.Daily(4, 30));

            // Every 15 minutes rather than nightly: a creator's thank-you that lands the next
            // morning reads as broken. Same */15 slot shape as the two stalled-media reconcilers.
            RecurringJob.AddOrUpdate<IArtistFollowerMessageService>(
                HangfireJobIds.SendArtistMessageEmails,
                service => service.SendPendingEmailsAsync(),
                "*/15 * * * *");

            // Push, every 5 minutes - more often than either email job because push has no
            // deliberate spacing to observe and is the channel a listener notices immediately.
            // It covers both release notifications and artist messages in one pass.
            RecurringJob.AddOrUpdate<IArtistPushDispatchService>(
                HangfireJobIds.DispatchArtistPushNotifications,
                service => service.DispatchPendingAsync(),
                "*/5 * * * *");

            // Nightly incremental backup of the Azure blob containers into their backup- copies.
            // 06:45 UTC sits clear of every other daily job (0:00, 1:30, 2:00, 2:30, 3:00, 3:15,
            // 3:45, 4:00, 5:00, 5:30, 6:00) and of the hourly jobs, which fire on the hour or at :20.
            // Restore is admin-triggered only and is deliberately never scheduled.
            RecurringJob.AddOrUpdate<IStorageBackupService>(
                HangfireJobIds.StorageBackup,
                service => service.RunRecurringBackupAsync(),
                "45 6 * * *");

            // Sweep temp files left by a process that died mid-batch. 07:10 UTC sits clear of every
            // other daily job (the last is 06:45), of the hourly ones (:00 and :20), and of the
            // reconciler's */15, which fires at :00, :15, :30 and :45.
            //
            // Daily rather than at startup, which is the tempting choice because process start is the
            // very event that orphans them: a file orphaned by a recycle is minutes old at the next
            // start, so the age threshold that makes sweeping a shared directory safe correctly
            // refuses to touch it. The sweep has to come back later.
            RecurringJob.AddOrUpdate<ITempFileCleanupService>(
                HangfireJobIds.CleanupOrphanedTempFiles,
                service => service.CleanupOrphanedTempFilesAsync(),
                "10 7 * * *");

            // Schedule nightly sitemap generation at 5 AM UTC
            // This runs after all other jobs to ensure sitemap reflects current state
            RecurringJob.AddOrUpdate<ISitemapService>(
                "generate-sitemap",
                service => service.GenerateSitemapAsync(),
                Cron.Daily(5));

            // Retry duration extraction for active songs whose metadata is missing TrackLength.
            // Dispatches probes to the audio-processing Function; the durations are written as the
            // results come back, not before this job returns.
            RecurringJob.AddOrUpdate<ITrackLengthRepairService>(
                "repair-missing-track-lengths",
                service => service.RepairMissingTrackLengthsAsync(),
                Cron.Daily(5, 30));

            // Audio processing spans two clouds now, so a lost queue message leaves an upload job
            // or an audit run stuck with nothing to finish it. This sweeps those up.
            RecurringJob.AddOrUpdate<ISongUploadJobReconciler>(
                HangfireJobIds.ReconcileStalledMediaProcessing,
                service => service.ReconcileAsync(),
                "*/15 * * * *");

            // The lyrics equivalent, and a stronger one: alignment runs as a Durable orchestration
            // whose instance id was recorded when it started, so this asks Azure what happened rather
            // than guessing from a timestamp. That is also the only way an orchestration which
            // succeeded but whose callback was lost ever gets finished instead of failed.
            RecurringJob.AddOrUpdate<ILyricsAlignmentJobReconciler>(
                HangfireJobIds.ReconcileStalledLyricsAlignment,
                service => service.ReconcileAsync(),
                "*/15 * * * *");

            // Schedule hourly retry of pending 1099 transactions
            // This retries TaxBandits 1099 reports that were deferred due to maintenance or failures
            RecurringJob.AddOrUpdate<IStreamPayoutService>(
                "retry-pending-1099-transactions",
                service => service.RetryPending1099TransactionsAsync(),
                Cron.Hourly());

            // Schedule hourly reset of expired maintenance windows
            // Resets site maintenance and Tax Bandits maintenance times to DateTime.MinValue once the end time passes
            RecurringJob.AddOrUpdate<IMaintenanceResetService>(
                "reset-expired-maintenance-windows",
                service => service.ResetExpiredMaintenanceWindowsAsync(),
                Cron.Hourly());

            _logger.LogInformation("Hangfire recurring jobs initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Hangfire recurring jobs");
            throw;
        }
    }
}
