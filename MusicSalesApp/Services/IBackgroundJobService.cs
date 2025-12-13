namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing background jobs
/// </summary>
public interface IBackgroundJobService
{
    /// <summary>
    /// Initialize all recurring Hangfire jobs
    /// </summary>
    void InitializeRecurringJobs();
}
