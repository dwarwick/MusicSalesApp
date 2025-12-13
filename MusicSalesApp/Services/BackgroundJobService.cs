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
            // Schedule daily cleanup job at 3 AM UTC
            RecurringJob.AddOrUpdate<IPlaylistCleanupService>(
                "cleanup-lapsed-subscription-playlists",
                service => service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync(),
                Cron.Daily(3));

            _logger.LogInformation("Hangfire recurring jobs initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Hangfire recurring jobs");
            throw;
        }
    }
}
