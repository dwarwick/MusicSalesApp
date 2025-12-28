using System.Net;
using System.Net.Mail;

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
        private readonly string _password;
        private readonly string _server;

        private readonly string _header;
        private readonly string _footer;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            var emailSettings = configuration.GetSection("EmailSettings");
            _domain = emailSettings["Domain"] ?? string.Empty;
            _fromEmail = emailSettings["CustomerServiceEmail"] ?? string.Empty;
            _password = emailSettings["Password"] ?? string.Empty;
            _server = emailSettings["Server"] ?? string.Empty;
            
            _header = $"<html xmlns=\"http://www.w3.org/1999/xhtml\" lang=\"en\" xml:lang=\"en\"><head></head><body><div align=\"center\"></div><h3 style=\"text-align: center;\">{_domain}</h3>";
            _footer = $"<div style=\"text-align:center;margin-top:20px;\">&#169; {DateTime.Now.Year} {_domain}</div></body></html>";

            _logger.LogInformation("EmailService initialized with domain: {Domain}", _domain);
        }

        /// <inheritdoc />
        public bool SendEmailVerificationMessage(string email, string tokenUrl)
        {
            _logger.LogInformation("Sending email verification to: {Email}", email);

            try
            {
                if (string.IsNullOrEmpty(_fromEmail) || string.IsNullOrEmpty(_password) || string.IsNullOrEmpty(_server))
                {
                    _logger.LogError("Email configuration is missing required values for verification email to {Email}", email);
                    return false;
                }

                var subject = "Email Verification";
                var body = $@"
                <h2>Verify Your Email</h2>
                <p>Thank you for registering. Please click the link below to verify your email address:</p>
                <p><a href='{tokenUrl}'>Verify Email</a></p>
                <p>If you didn't request this verification, please ignore this email.</p>
                ";

                return SendEmail(email, subject, body);
            }
            catch (SmtpException ex)
            {
                _logger.LogError(ex, "SMTP error sending verification email to {Email}: {Message}", email, ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending email verification message to {Email}: {Message}", email, ex.Message);
                return false;
            }
        }

        /// <inheritdoc />
        public bool SendPasswordResetEmail(string email, string tokenUrl, string baseUrl)
        {
            _logger.LogInformation("Sending password reset email to: {Email}", email);

            try
            {
                if (string.IsNullOrEmpty(_fromEmail) || string.IsNullOrEmpty(_password) || string.IsNullOrEmpty(_server))
                {
                    _logger.LogError("Email configuration is missing required values for password reset email to {Email}", email);
                    return false;
                }

                var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";
                var subject = "Password Reset Request";
                var body = $@"
                <div style='text-align: center; margin-bottom: 20px;'>
                    <img src='{logoUrl}' alt='Logo' style='max-width: 150px; height: auto;' />
                </div>
                <h2>Reset Your Password</h2>
                <p>You requested a password reset. Please click the link below to reset your password:</p>
                <p><a href='{tokenUrl}' style='display: inline-block; padding: 10px 20px; background-color: #007bff; color: white; text-decoration: none; border-radius: 5px;'>Reset Password</a></p>
                <p style='color: #666; font-size: 14px;'>This link will expire in <strong>10 minutes</strong>.</p>
                <p>If you didn't request a password reset, please ignore this email.</p>
                ";

                return SendEmail(email, subject, body);
            }
            catch (SmtpException ex)
            {
                _logger.LogError(ex, "SMTP error sending password reset email to {Email}: {Message}", email, ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending password reset email to {Email}: {Message}", email, ex.Message);
                return false;
            }
        }

        /// <inheritdoc />
        public async Task<bool> SendEmailAsync(string toEmail, string subject, string body)
        {
            // Bypass email sending for demo/anonymous users
            if (toEmail.StartsWith("DemoUser_", StringComparison.OrdinalIgnoreCase) || toEmail.StartsWith("anonymous_", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Bypassing email send for demo/anonymous user: {Email}", toEmail);
                return true;
            }

            _logger.LogInformation("Sending async email to: {Email} with subject: {Subject}", toEmail, subject);

            try
            {
                if (string.IsNullOrEmpty(_fromEmail) || string.IsNullOrEmpty(_password) || string.IsNullOrEmpty(_server))
                {
                    _logger.LogError("Email configuration is missing required values for async email to {Email}", toEmail);
                    return false;
                }

                return await Task.Run(() => SendEmail(toEmail, subject, body));
            }
            catch (SmtpException ex)
            {
                _logger.LogError(ex, "SMTP error sending async email to {Email}: {Message}", toEmail, ex.Message);
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Task canceled while sending async email to {Email}: {Message}", toEmail, ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending email asynchronously to {Email}: {Message}", toEmail, ex.Message);
                return false;
            }
        }

        private bool SendEmail(string toEmail, string subject, string body)
        {
            _logger.LogDebug("Attempting to send email from {FromEmail} to {ToEmail} via {Server}", _fromEmail, toEmail, _server);

            try
            {
                using var message = new MailMessage();
                message.From = new MailAddress(_fromEmail);
                message.Subject = subject;
                message.Body = _header + body + _footer;
                message.IsBodyHtml = true;
                message.To.Add(new MailAddress(toEmail));

                using var client = new SmtpClient(_server);
                client.Port = 587;
                client.Credentials = new NetworkCredential(_fromEmail, _password);
                client.EnableSsl = true;
                client.Timeout = 30000; // 30 seconds timeout

                client.Send(message);
                _logger.LogInformation("Email successfully sent to {ToEmail}", toEmail);
                return true;
            }
            catch (SmtpException ex)
            {
                _logger.LogError(ex, "SMTP error sending email to {ToEmail}: {StatusCode} - {Message}",
                    toEmail, ex.StatusCode, ex.Message);
                return false;
            }
            catch (FormatException ex)
            {
                _logger.LogError(ex, "Format error in email to {ToEmail}: {Message}", toEmail, ex.Message);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Invalid operation in email to {ToEmail}: {Message}", toEmail, ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending email to {ToEmail}: {Message}", toEmail, ex.Message);
                return false;
            }
        }
    }
}
