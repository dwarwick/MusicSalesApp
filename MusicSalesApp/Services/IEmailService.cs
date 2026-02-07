using MusicSalesApp.Models;

namespace MusicSalesApp.Services
{
    /// <summary>
    /// Service for sending emails to users.
    /// </summary>
    public interface IEmailService
    {
        /// <summary>
        /// Sends an email verification message with a link to verify the user's email address.
        /// </summary>
        /// <param name="email">The recipient's email address.</param>
        /// <param name="tokenUrl">The complete verification URL including the token.</param>
        /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
        /// <returns>True if the email was sent successfully, false otherwise.</returns>
        bool SendEmailVerificationMessage(string email, string tokenUrl, string baseUrl);

        /// <summary>
        /// Sends an email verification message with detailed result information.
        /// </summary>
        /// <param name="email">The recipient's email address.</param>
        /// <param name="tokenUrl">The complete verification URL including the token.</param>
        /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
        /// <returns>An EmailResult with detailed success/failure information.</returns>
        EmailResult SendEmailVerificationWithResult(string email, string tokenUrl, string baseUrl);

        /// <summary>
        /// Sends a password reset email with a link to reset the user's password.
        /// </summary>
        /// <param name="email">The recipient's email address.</param>
        /// <param name="tokenUrl">The complete password reset URL including the token.</param>
        /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
        /// <returns>True if the email was sent successfully, false otherwise.</returns>
        bool SendPasswordResetEmail(string email, string tokenUrl, string baseUrl);

        /// <summary>
        /// Sends a password reset email with detailed result information.
        /// </summary>
        /// <param name="email">The recipient's email address.</param>
        /// <param name="tokenUrl">The complete password reset URL including the token.</param>
        /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
        /// <returns>An EmailResult with detailed success/failure information.</returns>
        EmailResult SendPasswordResetWithResult(string email, string tokenUrl, string baseUrl);

        /// <summary>
        /// Sends an email asynchronously with the specified subject and body.
        /// </summary>
        /// <param name="toEmail">The recipient's email address.</param>
        /// <param name="subject">The subject of the email.</param>
        /// <param name="body">The HTML body content of the email.</param>
        /// <returns>True if the email was sent successfully, false otherwise.</returns>
        Task<bool> SendEmailAsync(string toEmail, string subject, string body);

        /// <summary>
        /// Sends an email asynchronously with detailed result information.
        /// </summary>
        /// <param name="toEmail">The recipient's email address.</param>
        /// <param name="subject">The subject of the email.</param>
        /// <param name="body">The HTML body content of the email.</param>
        /// <returns>An EmailResult with detailed success/failure information.</returns>
        Task<EmailResult> SendEmailWithResultAsync(string toEmail, string subject, string body);

        /// <summary>
        /// Gets the application base URL from configuration, defaulting to https://streamtunes.net.
        /// Use this method when constructing URLs for email content (e.g., logo images, links).
        /// </summary>
        /// <returns>The application base URL.</returns>
        string GetAppBaseUrl();

        /// <summary>
        /// Gets the absolute URL for the StreamTunes logo image, using the canonical app base URL.
        /// Use this in all email templates instead of constructing the logo URL manually.
        /// </summary>
        /// <returns>The full logo image URL (e.g., https://streamtunes.net/images/logo-light-small.png).</returns>
        string GetLogoUrl();

        /// <summary>
        /// Gets the standard HTML block for the StreamTunes logo to embed at the top of emails.
        /// Uses the canonical app base URL to ensure the logo always loads correctly.
        /// </summary>
        /// <returns>An HTML string containing a centered logo image block.</returns>
        string GetEmailLogoHtml();
    }
}