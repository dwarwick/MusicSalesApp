using Hangfire;

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
            // This processes payouts to sellers based on stream counts
            RecurringJob.AddOrUpdate<IStreamPayoutService>(
                "process-stream-payouts",
                service => service.ProcessPendingPayoutsAsync(),
                Cron.Weekly(DayOfWeek.Monday, 1));

            // Schedule nightly sync of likes to Supabase at 2 AM UTC
            RecurringJob.AddOrUpdate<IRecommendationService>(
                "sync-likes-to-supabase",
                service => service.SyncLikesToSupabaseAsync(),
                Cron.Daily(2));

            // Schedule daily cleanup job at 3 AM UTC
            RecurringJob.AddOrUpdate<IPlaylistCleanupService>(
                "cleanup-lapsed-subscription-playlists",
                service => service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync(),
                Cron.Daily(3));

            // Schedule nightly new song notification emails at 4 AM UTC
            // This runs after song cleanup and sends emails to opted-in users about new songs added in the past 24 hours
            RecurringJob.AddOrUpdate<INewSongNotificationService>(
                "send-new-song-notification-emails",
                service => service.SendNewSongNotificationsAsync(),
                Cron.Daily(4));

            // Schedule nightly sitemap generation at 5 AM UTC
            // This runs after all other jobs to ensure sitemap reflects current state
            RecurringJob.AddOrUpdate<ISitemapService>(
                "generate-sitemap",
                service => service.GenerateSitemapAsync(),
                Cron.Daily(5));

            _logger.LogInformation("Hangfire recurring jobs initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Hangfire recurring jobs");
            throw;
        }
    }
}
