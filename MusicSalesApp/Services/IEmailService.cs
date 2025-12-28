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
        /// <returns>True if the email was sent successfully, false otherwise.</returns>
        bool SendEmailVerificationMessage(string email, string tokenUrl);

        /// <summary>
        /// Sends a password reset email with a link to reset the user's password.
        /// </summary>
        /// <param name="email">The recipient's email address.</param>
        /// <param name="tokenUrl">The complete password reset URL including the token.</param>
        /// <param name="baseUrl">The base URL for constructing the logo image URL.</param>
        /// <returns>True if the email was sent successfully, false otherwise.</returns>
        bool SendPasswordResetEmail(string email, string tokenUrl, string baseUrl);

        /// <summary>
        /// Sends an email asynchronously with the specified subject and body.
        /// </summary>
        /// <param name="toEmail">The recipient's email address.</param>
        /// <param name="subject">The subject of the email.</param>
        /// <param name="body">The HTML body content of the email.</param>
        /// <returns>True if the email was sent successfully, false otherwise.</returns>
        Task<bool> SendEmailAsync(string toEmail, string subject, string body);
    }
}