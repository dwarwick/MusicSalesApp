using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services
{
    /// <summary>
    /// Service for sending emails including verification and password reset emails.
    /// </summary>
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        private readonly string _domain;
        private readonly string _fromEmail;
        private readonly string _displayName;
        private readonly string _userName;
        private readonly string _password;
        private readonly string _server;        
    
        // Spam filter error message patterns - more specific to avoid false positives
        // These patterns match common SMTP server responses for spam-related rejections
        private static readonly string[] SpamFilterPatterns = new[]
        {
            "spam filter",
            "due to spam",
            "blocked for spam",
            "rejected for spam",
            "blacklist",
            "not accepted due to"
        };

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            var emailSettings = configuration.GetSection("EmailSettings");
            _domain = emailSettings["Domain"] ?? string.Empty;
            _fromEmail = emailSettings["CustomerServiceEmail"] ?? string.Empty;
            _displayName = emailSettings["DisplayName"] ?? "StreamTunes";
            _userName = emailSettings["Username"] ?? string.Empty;
            _password = emailSettings["Password"] ?? string.Empty;
            _server = emailSettings["Server"] ?? string.Empty;

            _logger.LogInformation("EmailService initialized with domain: {Domain}", _domain);
        }

        /// <summary>
        /// Checks if an SMTP exception message indicates a spam filter rejection.
        /// Uses specific patterns to avoid false positives from generic SMTP error messages.
        /// </summary>
        private static bool IsSpamFilterError(SmtpException ex)
        {
            // Only check TransactionFailed status which is commonly used for spam rejections
            if (ex.StatusCode != SmtpStatusCode.TransactionFailed)
            {
                return false;
            }

            var message = ex.Message.ToLowerInvariant();
            return SpamFilterPatterns.Any(pattern => message.Contains(pattern));
        }

        /// <inheritdoc />
        public bool SendEmailVerificationMessage(string email, string tokenUrl, string baseUrl)
        {
            return SendEmailVerificationWithResult(email, tokenUrl, baseUrl).Success;
        }

        /// <inheritdoc />
        public EmailResult SendEmailVerificationWithResult(string email, string tokenUrl, string baseUrl)
        {
            _logger.LogInformation("Sending email verification to: {Email}", email);

            try
            {
                if (string.IsNullOrEmpty(_fromEmail) || string.IsNullOrEmpty(_password) || string.IsNullOrEmpty(_server))
                {
                    _logger.LogError("Email configuration is missing required values for verification email to {Email}", email);
                    return EmailResult.MissingConfiguration();
                }

                var logoHtml = GetEmailLogoHtml();
                var subject = "Email Verification";
                var body = $@"
                {logoHtml}
                <h2>Verify Your Email</h2>
                <p>Thank you for registering with StreamTunes. Please click the link below to verify your email address:</p>
                <p><a href='{tokenUrl}' style='display: inline-block; padding: 10px 20px; background-color: #1a1a2e; color: white; text-decoration: none; border-radius: 5px;'>Verify Email</a></p>
                <p style='color: #666; font-size: 14px;'>This link will expire in <strong>10 minutes</strong>.</p>
                <p>If you didn't request this verification, please ignore this email.</p>
                ";

                return SendEmailWithResult(email, subject, body);
            }
            catch (SmtpException ex)
            {
                if (IsSpamFilterError(ex))
                {
                    _logger.LogError(ex, "Verification email rejected by spam filter for {Email}: {Message}", email, ex.Message);
                    return EmailResult.SpamFilterRejected();
                }
                _logger.LogError(ex, "SMTP error sending verification email to {Email}: {Message}", email, ex.Message);
                return EmailResult.SmtpError(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending email verification message to {Email}: {Message}", email, ex.Message);
                return EmailResult.UnexpectedError(ex.Message);
            }
        }

        /// <inheritdoc />
        public bool SendPasswordResetEmail(string email, string tokenUrl, string baseUrl)
        {
            return SendPasswordResetWithResult(email, tokenUrl, baseUrl).Success;
        }

        /// <inheritdoc />
        public EmailResult SendPasswordResetWithResult(string email, string tokenUrl, string baseUrl)
        {
            _logger.LogInformation("Sending password reset email to: {Email}", email);

            try
            {
                if (string.IsNullOrEmpty(_fromEmail) || string.IsNullOrEmpty(_password) || string.IsNullOrEmpty(_server))
                {
                    _logger.LogError("Email configuration is missing required values for password reset email to {Email}", email);
                    return EmailResult.MissingConfiguration();
                }

                var logoHtml = GetEmailLogoHtml();
                var subject = "Password Reset Request";
                var body = $@"
                {logoHtml}
                <h2>Reset Your Password</h2>
                <p>You requested a password reset. Please click the link below to reset your password:</p>
                <p><a href='{tokenUrl}' style='display: inline-block; padding: 10px 20px; background-color: #007bff; color: white; text-decoration: none; border-radius: 5px;'>Reset Password</a></p>
                <p style='color: #666; font-size: 14px;'>This link will expire in <strong>10 minutes</strong>.</p>
                <p>If you didn't request a password reset, please ignore this email.</p>
                ";

                return SendEmailWithResult(email, subject, body);
            }
            catch (SmtpException ex)
            {
                if (IsSpamFilterError(ex))
                {
                    _logger.LogError(ex, "Password reset email rejected by spam filter for {Email}: {Message}", email, ex.Message);
                    return EmailResult.SpamFilterRejected();
                }
                _logger.LogError(ex, "SMTP error sending password reset email to {Email}: {Message}", email, ex.Message);
                return EmailResult.SmtpError(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending password reset email to {Email}: {Message}", email, ex.Message);
                return EmailResult.UnexpectedError(ex.Message);
            }
        }

        /// <inheritdoc />
        public async Task<bool> SendEmailAsync(string toEmail, string subject, string body)
        {
            var result = await SendEmailWithResultAsync(toEmail, subject, body);
            return result.Success;
        }

        /// <inheritdoc />
        public async Task<EmailResult> SendEmailWithResultAsync(string toEmail, string subject, string body)
        {
            // Bypass email sending for demo/anonymous users
            if (toEmail.StartsWith("DemoUser_", StringComparison.OrdinalIgnoreCase) || toEmail.StartsWith("anonymous_", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Bypassing email send for demo/anonymous user: {Email}", toEmail);
                return EmailResult.Succeeded();
            }

            _logger.LogInformation("Sending async email to: {Email} with subject: {Subject}", toEmail, subject);

            try
            {
                if (string.IsNullOrEmpty(_fromEmail) || string.IsNullOrEmpty(_password) || string.IsNullOrEmpty(_server))
                {
                    _logger.LogError("Email configuration is missing required values for async email to {Email}", toEmail);
                    return EmailResult.MissingConfiguration();
                }

                // Use the internal async method directly for proper async handling
                return await SendEmailWithResultInternalAsync(toEmail, subject, body);
            }
            catch (SmtpException ex)
            {
                if (IsSpamFilterError(ex))
                {
                    _logger.LogError(ex, "Async email rejected by spam filter for {Email}: {Message}", toEmail, ex.Message);
                    return EmailResult.SpamFilterRejected();
                }
                _logger.LogError(ex, "SMTP error sending async email to {Email}: {Message}", toEmail, ex.Message);
                return EmailResult.SmtpError(ex.Message);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Task canceled while sending async email to {Email}: {Message}", toEmail, ex.Message);
                return EmailResult.UnexpectedError("Email operation was canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending email asynchronously to {Email}: {Message}", toEmail, ex.Message);
                return EmailResult.UnexpectedError(ex.Message);
            }
        }

        private bool SendEmail(string toEmail, string subject, string body)
        {
            return SendEmailWithResult(toEmail, subject, body).Success;
        }

        private EmailResult SendEmailWithResult(string toEmail, string subject, string body)
        {
            // For synchronous calls, use the async version with a blocking wait
            return SendEmailWithResultInternalAsync(toEmail, subject, body).GetAwaiter().GetResult();
        }

        private async Task<EmailResult> SendEmailWithResultInternalAsync(string toEmail, string subject, string body)
        {
            _logger.LogInformation("Attempting to send email from {FromEmail} to {ToEmail} via {Server}", _fromEmail, toEmail, _server);

            try
            {
                _logger.LogDebug("Building email message for {ToEmail}", toEmail);
                
                using var message = new MailMessage();
                message.From = new MailAddress(_fromEmail, _displayName);
                message.Subject = subject;
                message.To.Add(new MailAddress(toEmail));
                
                // Generate a unique Message-ID to improve deliverability
                var messageId = $"<{Guid.NewGuid():N}@{_domain}>";
                message.Headers.Add("Message-ID", messageId);
                
                // Add standard headers to reduce spam score
                message.Headers.Add("X-Mailer", "StreamTunes");
                message.Headers.Add("X-Priority", "3");
                
                // Build proper HTML with DOCTYPE
                var fullHtmlBody = BuildHtmlEmail(body);
                
                // Create plain text version for multipart MIME
                var plainTextBody = ConvertHtmlToPlainText(body);
                
                // Add both plain text and HTML views (multipart/alternative)
                var plainView = AlternateView.CreateAlternateViewFromString(plainTextBody, System.Text.Encoding.UTF8, "text/plain");
                var htmlView = AlternateView.CreateAlternateViewFromString(fullHtmlBody, System.Text.Encoding.UTF8, "text/html");
                
                message.AlternateViews.Add(plainView);
                message.AlternateViews.Add(htmlView);

                _logger.LogDebug("Connecting to SMTP server {Server}:587 for {ToEmail}", _server, toEmail);

                using var client = new SmtpClient(_server);
                client.Port = 587;
                client.Credentials = new NetworkCredential(_userName, _password);
                client.EnableSsl = true;
                client.DeliveryMethod = SmtpDeliveryMethod.Network;
                client.Timeout = 30000; // 30 seconds timeout

                // Use SendMailAsync with a cancellation token for reliable timeout handling
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                
                _logger.LogDebug("Sending email via SendMailAsync to {ToEmail}", toEmail);
                
                try
                {
                    // ConfigureAwait(false) is critical here: this method can be called
                    // synchronously via .GetAwaiter().GetResult() from SendEmailWithResult.
                    // In Blazor Server, the RendererSynchronizationContext is active during
                    // component event handlers. Without ConfigureAwait(false), the await
                    // captures that context and tries to resume on it — but the sync context
                    // thread is blocked by GetResult(), causing a deadlock.
                    await client.SendMailAsync(message, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    _logger.LogError("Email send timed out after 30 seconds for {ToEmail}", toEmail);
                    return EmailResult.UnexpectedError("Email send operation timed out after 30 seconds.");
                }
                
                _logger.LogInformation("Email successfully sent to {ToEmail}", toEmail);
                return EmailResult.Succeeded();
            }
            catch (SmtpException ex)
            {
                if (IsSpamFilterError(ex))
                {
                    _logger.LogError(ex, "Email rejected by spam filter for {ToEmail}: {StatusCode} - {Message}",
                        toEmail, ex.StatusCode, ex.Message);
                    return EmailResult.SpamFilterRejected();
                }

                _logger.LogError(ex, "SMTP error sending email to {ToEmail}: {StatusCode} - {Message}",
                    toEmail, ex.StatusCode, ex.Message);
                return EmailResult.SmtpError(ex.Message);
            }
            catch (FormatException ex)
            {
                _logger.LogError(ex, "Format error in email to {ToEmail}: {Message}", toEmail, ex.Message);
                return EmailResult.UnexpectedError(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Invalid operation in email to {ToEmail}: {Message}", toEmail, ex.Message);
                return EmailResult.UnexpectedError(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending email to {ToEmail}: {Message}", toEmail, ex.Message);
                return EmailResult.UnexpectedError(ex.Message);
            }
        }

        /// <summary>
        /// Builds a complete HTML email with proper DOCTYPE and structure
        /// </summary>
        private string BuildHtmlEmail(string bodyContent)
        {
            return $@"<!DOCTYPE html>
<html xmlns=""http://www.w3.org/1999/xhtml"" lang=""en"">
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <meta http-equiv=""Content-Type"" content=""text/html; charset=UTF-8"">
    <title>{_displayName}</title>
</head>
<body style=""font-family: Arial, Helvetica, sans-serif; line-height: 1.6; color: #333333; max-width: 600px; margin: 0 auto; padding: 20px; background-color: #f9f9f9;"">
    <div style=""background-color: #ffffff; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1);"">
        {bodyContent}
        <hr style=""border: none; border-top: 1px solid #eeeeee; margin: 30px 0;"" />
        <p style=""text-align: center; color: #999999; font-size: 12px; margin: 0;"">
            &copy; {DateTime.Now.Year} {_displayName}. All rights reserved.
        </p>
    </div>
</body>
</html>";
        }

        /// <summary>
        /// Converts HTML to plain text for multipart email alternative
        /// </summary>
        private static string ConvertHtmlToPlainText(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;

            var text = html;
            
            // Convert links to text with URL
            text = Regex.Replace(text, @"<a[^>]+href=[""']([^""']+)[""'][^>]*>([^<]+)</a>", "$2: $1", RegexOptions.IgnoreCase);
            
            // Convert headers to plain text with newlines
            text = Regex.Replace(text, @"<h[1-6][^>]*>([^<]*)</h[1-6]>", "\n$1\n", RegexOptions.IgnoreCase);
            
            // Convert paragraphs and divs to newlines
            text = Regex.Replace(text, @"<(p|div)[^>]*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</(p|div)>", "\n", RegexOptions.IgnoreCase);
            
            // Convert line breaks
            text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            
            // Remove style and script blocks
            text = Regex.Replace(text, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            
            // Remove remaining HTML tags
            text = Regex.Replace(text, @"<[^>]+>", "");
            
            // Decode HTML entities
            text = WebUtility.HtmlDecode(text);
            
            // Normalize whitespace - collapse multiple spaces but preserve newlines
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @"\n\s*\n\s*\n", "\n\n");
            
            return text.Trim();
        }

        /// <inheritdoc />
        public string GetAppBaseUrl()
        {
            return _configuration["App:BaseUrl"] ?? "https://streamtunes.net";
        }

        /// <inheritdoc />
        public string GetLogoUrl()
        {
            return $"{GetAppBaseUrl().TrimEnd('/')}/images/logo-light-small.png";
        }

        /// <inheritdoc />
        public string GetEmailLogoHtml()
        {
            var logoUrl = GetLogoUrl();
            return $@"
            <div style='text-align: center; margin-bottom: 20px;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
            </div>";
        }
    }
}
