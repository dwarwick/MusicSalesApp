namespace MusicSalesApp.Services;

/// <summary>
/// Service for sending account-related confirmation emails.
/// </summary>
public interface IAccountEmailService
{
    /// <summary>
    /// Sends a welcome email when a user creates an account.
    /// </summary>
    /// <param name="userEmail">The recipient's email address.</param>
    /// <param name="userName">The user's display name (or email if name not available).</param>
    /// <param name="baseUrl">The base URL for constructing asset URLs.</param>
    /// <returns>True if the email was sent successfully, false otherwise.</returns>
    Task<bool> SendAccountCreatedEmailAsync(string userEmail, string userName, string baseUrl);

    /// <summary>
    /// Sends a confirmation email when a user suspends (closes) their account.
    /// </summary>
    /// <param name="userEmail">The recipient's email address.</param>
    /// <param name="userName">The user's display name (or email if name not available).</param>
    /// <param name="baseUrl">The base URL for constructing asset URLs.</param>
    /// <returns>True if the email was sent successfully, false otherwise.</returns>
    Task<bool> SendAccountClosedEmailAsync(string userEmail, string userName, string baseUrl);

    /// <summary>
    /// Sends a confirmation email when a user changes their password.
    /// </summary>
    /// <param name="userEmail">The recipient's email address.</param>
    /// <param name="userName">The user's display name (or email if name not available).</param>
    /// <param name="baseUrl">The base URL for constructing asset URLs.</param>
    /// <returns>True if the email was sent successfully, false otherwise.</returns>
    Task<bool> SendPasswordChangedEmailAsync(string userEmail, string userName, string baseUrl);

    /// <summary>
    /// Sends a confirmation email when a user deletes their account.
    /// </summary>
    /// <param name="userEmail">The recipient's email address.</param>
    /// <param name="userName">The user's display name (or email if name not available).</param>
    /// <param name="baseUrl">The base URL for constructing asset URLs.</param>
    /// <returns>True if the email was sent successfully, false otherwise.</returns>
    Task<bool> SendAccountDeletedEmailAsync(string userEmail, string userName, string baseUrl);

    /// <summary>
    /// Sends a confirmation email when a user cancels their subscription.
    /// </summary>
    /// <param name="userEmail">The recipient's email address.</param>
    /// <param name="userName">The user's display name (or email if name not available).</param>
    /// <param name="endDate">The date when the subscription will end.</param>
    /// <param name="baseUrl">The base URL for constructing asset URLs.</param>
    /// <returns>True if the email was sent successfully, false otherwise.</returns>
    Task<bool> SendSubscriptionCancelledEmailAsync(string userEmail, string userName, DateTime? endDate, string baseUrl);

    /// <summary>
    /// Sends a confirmation email when a user's account is reactivated (un-suspended).
    /// </summary>
    /// <param name="userEmail">The recipient's email address.</param>
    /// <param name="userName">The user's display name (or email if name not available).</param>
    /// <param name="baseUrl">The base URL for constructing asset URLs.</param>
    /// <returns>True if the email was sent successfully, false otherwise.</returns>
    Task<bool> SendAccountReactivatedEmailAsync(string userEmail, string userName, string baseUrl);
}
