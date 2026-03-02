#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for sending admin notification emails and recording user history events.
/// </summary>
public interface IAdminNotificationService
{
    /// <summary>
    /// Notifies admin about a user registration event.
    /// </summary>
    Task NotifyUserRegisteredAsync(string userEmail);

    /// <summary>
    /// Notifies admin about a user email confirmation event.
    /// </summary>
    Task NotifyEmailConfirmedAsync(string userEmail);

    /// <summary>
    /// Notifies admin about a user completing their W8/W9 tax form.
    /// </summary>
    Task NotifyTaxFormCompletedAsync(string userEmail, string formType);

    /// <summary>
    /// Notifies admin about a user gaining creator status.
    /// </summary>
    Task NotifyCreatorStatusGainedAsync(string userEmail);

    /// <summary>
    /// Notifies admin about a user losing creator status.
    /// </summary>
    Task NotifyCreatorStatusLostAsync(string userEmail);

    /// <summary>
    /// Notifies admin about a song upload. Uses the same format as new song notifications
    /// but includes the uploader's email address.
    /// </summary>
    Task NotifyUploadCompletedAsync(string userEmail, string fileName, bool hasCoverArt);

    /// <summary>
    /// Notifies admin about a song rename.
    /// </summary>
    Task NotifySongRenamedAsync(string userEmail, string oldTitle, string newTitle);

    /// <summary>
    /// Notifies admin about a song art update.
    /// </summary>
    Task NotifySongArtUpdatedAsync(string userEmail, string songTitle);

    /// <summary>
    /// Records a user history event in the database.
    /// </summary>
    Task RecordUserHistoryAsync(int userId, string userEmail, string eventType, string description, string? oldValue = null, string? newValue = null);

    /// <summary>
    /// Gets all user history records, ordered by most recent first.
    /// </summary>
    Task<List<MusicSalesApp.Models.UserHistory>> GetAllUserHistoryAsync();

    /// <summary>
    /// Checks if a specific admin notification type is enabled.
    /// </summary>
    Task<bool> IsNotificationEnabledAsync(string settingKey);

    /// <summary>
    /// Sets whether a specific admin notification type is enabled.
    /// </summary>
    Task SetNotificationEnabledAsync(string settingKey, bool enabled);
}
