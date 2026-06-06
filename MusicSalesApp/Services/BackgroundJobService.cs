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

            // Schedule nightly sitemap generation at 5 AM UTC
            // This runs after all other jobs to ensure sitemap reflects current state
            RecurringJob.AddOrUpdate<ISitemapService>(
                "generate-sitemap",
                service => service.GenerateSitemapAsync(),
                Cron.Daily(5));

            // Retry duration extraction for active songs whose metadata is missing TrackLength.
            RecurringJob.AddOrUpdate<ITrackLengthRepairService>(
                "repair-missing-track-lengths",
                service => service.RepairMissingTrackLengthsAsync(),
                Cron.Daily(5, 30));

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
