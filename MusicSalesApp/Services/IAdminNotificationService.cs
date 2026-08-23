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
    /// Sends a batch upload summary email to admin with song titles and cover art images,
    /// similar to the daily new song digest. Also sends a confirmation email to the creator.
    /// Called once after all uploads in a session complete.
    /// </summary>
    /// <param name="userEmail">The creator's email address.</param>
    /// <param name="creatorId">The creator's ID for looking up uploaded songs.</param>
    /// <param name="uploadedBlobPaths">List of MP3 blob paths that were uploaded (for exact matching).</param>
    Task NotifyUploadBatchCompletedAsync(string userEmail, int creatorId, List<string> uploadedBlobPaths);

    /// <summary>
    /// Notifies admin about a song rename.
    /// </summary>
    Task NotifySongRenamedAsync(string userEmail, string oldTitle, string newTitle);

    /// <summary>
    /// Notifies admin about a song art update.
    /// </summary>
    Task NotifySongArtUpdatedAsync(string userEmail, string songTitle);

    /// <summary>
    /// Notifies admin that a creator submitted lyrics for a song, and records a user history event.
    /// </summary>
    /// <remarks>
    /// Takes ids rather than an email and a title because the caller - <c>SongLyricsService</c> -
    /// holds neither, and this service already opens a context in every method.
    /// </remarks>
    Task NotifyLyricsAddedAsync(int creatorId, int songMetadataId);

    /// <summary>
    /// Notifies admin that a creator published timed lyrics, and records a user history event.
    /// </summary>
    Task NotifyLyricsPublishedAsync(int creatorId, int songMetadataId);

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

    /// <summary>
    /// Notifies admin about a tip fraud prevention event.
    /// </summary>
    Task NotifyTipFraudPreventedAsync(string tipperEmail, string creatorName, string creatorEmail, decimal amount, string fraudRule, string reason);

    /// <summary>
    /// Notifies admin and the creator when a new persona is created.
    /// Records a user history event.
    /// </summary>
    Task NotifyPersonaCreatedAsync(int creatorId, string creatorEmail, string personaName, string? bio, string? personaImageUrl);

    /// <summary>
    /// Notifies admin and the creator when a persona is updated.
    /// Records a user history event.
    /// </summary>
    Task NotifyPersonaUpdatedAsync(int creatorId, string creatorEmail, string personaName, string? bio, string? personaImageUrl);

    /// <summary>
    /// Notifies admin and the creator when a persona is deleted.
    /// Records a user history event.
    /// </summary>
    Task NotifyPersonaDeletedAsync(int creatorId, string creatorEmail, string personaName, string? bio, string? personaImageUrl);
}
