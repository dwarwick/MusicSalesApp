using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for sending notifications about new songs to users who have opted in.
/// </summary>
public interface INewSongNotificationService
{
    /// <summary>
    /// Sends notification emails about new songs added in the past 24 hours to opted-in users.
    /// This method is designed to be called by a nightly Hangfire job.
    /// Emails are sent in batches with delays to avoid spam filter issues.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SendNewSongNotificationsAsync();

    /// <summary>
    /// Gets the list of songs and albums added in the specified time period.
    /// </summary>
    /// <param name="since">The start date/time to check for new songs.</param>
    /// <returns>A list of song metadata for newly added content.</returns>
    Task<List<SongMetadata>> GetNewSongsAsync(DateTime since);
}
