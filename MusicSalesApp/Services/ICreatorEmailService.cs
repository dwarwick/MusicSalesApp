#nullable enable
namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for sending emails related to creator operations.
/// Handles tax form (W-9/W-8) webhook notifications and creator onboarding communications.
/// </summary>
public interface ICreatorEmailService
{
    /// <summary>
    /// Sends an email to the creator when we receive a W-9/W-8 webhook response.
    /// Notifies them that we received the response and are analyzing it.
    /// </summary>
    /// <param name="userEmail">The creator's email address.</param>
    /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
    /// <param name="formType">The form type: "W-9" or "W-8".</param>
    /// <returns>True if the email was sent successfully.</returns>
    Task<bool> SendTaxFormReceivedEmailAsync(string userEmail, string baseUrl, string formType);

    /// <summary>
    /// Sends an email to the creator when there is an error processing the webhook response.
    /// Also sends an email to admin with error details.
    /// </summary>
    /// <param name="userEmail">The creator's email address.</param>
    /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
    /// <param name="submissionId">The submission ID (for admin reference).</param>
    /// <param name="errorDetails">The error details (for admin only, not sent to user).</param>
    /// <returns>True if both emails were sent successfully.</returns>
    Task<bool> SendTaxFormProcessingErrorEmailAsync(string userEmail, string baseUrl, string? submissionId, string errorDetails);

    /// <summary>
    /// Sends an email to the creator when the W-9/W-8 creation failed.
    /// This includes TIN match failures or other form validation failures.
    /// </summary>
    /// <param name="userEmail">The creator's email address.</param>
    /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
    /// <param name="formType">The form type: "W-9" or "W-8".</param>
    /// <param name="failureReason">A user-friendly reason for the failure (optional).</param>
    /// <returns>True if the email was sent successfully.</returns>
    Task<bool> SendTaxFormFailedEmailAsync(string userEmail, string baseUrl, string formType, string? failureReason = null);

    /// <summary>
    /// Sends a welcome email to the creator when the W-9/W-8 creation was successful.
    /// Also sends a notification email to admin with user details and form type.
    /// </summary>
    /// <param name="userEmail">The creator's email address.</param>
    /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
    /// <param name="formType">The form type: "W-9" or "W-8".</param>
    /// <param name="countryCode">For W-8 forms, the ISO-2 country code of the creator's residence.</param>
    /// <returns>True if both emails were sent successfully.</returns>
    Task<bool> SendTaxFormSuccessEmailAsync(string userEmail, string baseUrl, string formType, string? countryCode = null);

    /// <summary>
    /// Sends an email to the creator when TIN verification was rejected.
    /// Advises them to enter their legal name and ensure their SSN or EIN is correct and try again.
    /// </summary>
    /// <param name="userEmail">The creator's email address.</param>
    /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
    /// <returns>True if the email was sent successfully.</returns>
    Task<bool> SendTinRejectedEmailAsync(string userEmail, string baseUrl);

    /// <summary>
    /// Sends an email to the creator when TIN verification is pending.
    /// Lets them know there is a delay in verifying their information.
    /// Also sends an email to admin with the user email and submission ID.
    /// </summary>
    /// <param name="userEmail">The creator's email address.</param>
    /// <param name="submissionId">The W9 submission ID (for admin reference).</param>
    /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
    /// <returns>True if both emails were sent successfully.</returns>
    Task<bool> SendTinPendingEmailAsync(string userEmail, string? submissionId, string baseUrl);

    /// <summary>
    /// Sends an email to the creator when their tax form status is changed by an admin.
    /// This is sent from the customer service email address.
    /// </summary>
    /// <param name="userEmail">The creator's email address.</param>
    /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
    /// <param name="previousStatus">The previous tax form status.</param>
    /// <param name="newStatus">The new tax form status.</param>
    /// <returns>True if the email was sent successfully.</returns>
    Task<bool> SendTaxStatusChangedEmailAsync(string userEmail, string baseUrl, string previousStatus, string newStatus);
}
