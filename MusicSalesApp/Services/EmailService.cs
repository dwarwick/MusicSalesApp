using System.Net;
using System.Net.Mail;

namespace MusicSalesApp.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        private readonly string domain;
        private readonly string fromEmail;

        private readonly string header;
        private readonly string footer;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            domain = configuration["EmailSettings:Domain"] ?? string.Empty;
            fromEmail = configuration["EmailSettings:CustomerServiceEmail"] ?? string.Empty;
            header = $"<html xmlns=\"http://www.w3.org/1999/xhtml\" lang=\"en\" xml:lang=\"en\"<head></head><body><div align=\"center\"></div><h3 style=\"text-align: center;\">{domain}</h3>";
            footer = $"<div style=\"text-align:center;margin-top:20px;\">&#169; {DateTime.Now.Year} {domain}</div></body></html>";

            _logger.LogInformation("EmailService initialized with domain: {Domain}", domain);
        }

        public bool SendEmailVerificationMessage(string email, string tokenUrl)
        {
            _logger.LogInformation("Sending email verification to: {Email}", email);

            try
            {
                var emailSettings = _configuration.GetSection("EmailSettings");
                var fromEmail = emailSettings["CustomerServiceEmail"] ?? string.Empty;
                var password = emailSettings["Password"] ?? string.Empty;
                var server = emailSettings["Server"] ?? string.Empty;
                var domain = emailSettings["Domain"] ?? string.Empty;

                if (string.IsNullOrEmpty(fromEmail) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(server))
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

                return SendEmail(fromEmail, password, server, email, subject, body);
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

        public bool SendPasswordResetEmail(string email, string tokenUrl)
        {
            _logger.LogInformation("Sending password reset email to: {Email}", email);

            try
            {
                var emailSettings = _configuration.GetSection("EmailSettings");
                var fromEmail = emailSettings["CustomerServiceEmail"] ?? string.Empty;
                var password = emailSettings["Password"] ?? string.Empty;
                var server = emailSettings["Server"] ?? string.Empty;
                var domain = emailSettings["Domain"] ?? string.Empty;

                if (string.IsNullOrEmpty(fromEmail) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(server))
                {
                    _logger.LogError("Email configuration is missing required values for password reset email to {Email}", email);
                    return false;
                }

                var subject = "Password Reset Request";
                var body = $@"
                <h2>Reset Your Password</h2>
                <p>You requested a password reset. Please click the link below to reset your password:</p>
                <p><a href='{tokenUrl}'>Reset Password</a></p>
                <p>If you didn't request a password reset, please ignore this email.</p>
                ";

                return SendEmail(fromEmail, password, server, email, subject, body);
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

        public async Task<bool> SendEmailAsync(string toEmail, string subject, string body)
        {
            if (toEmail.StartsWith("DemoUser_", StringComparison.OrdinalIgnoreCase) || toEmail.StartsWith("anonymous_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _logger.LogInformation("Sending async email to: {Email} with subject: {Subject}", toEmail, subject);

            try
            {
                var emailSettings = _configuration.GetSection("EmailSettings");
                var fromEmail = emailSettings["CustomerServiceEmail"] ?? string.Empty;
                var password = emailSettings["Password"] ?? string.Empty;
                var server = emailSettings["Server"] ?? string.Empty;
                var domain = emailSettings["Domain"] ?? string.Empty;

                if (string.IsNullOrEmpty(fromEmail) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(server))
                {
                    _logger.LogError("Email configuration is missing required values for async email to {Email}", toEmail);
                    return false;
                }

                return await Task.Run(() => SendEmail(fromEmail, password, server, toEmail, subject, body));
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

        private bool SendEmail(string fromEmail, string password, string server, string toEmail, string subject, string body)
        {
            _logger.LogDebug("Attempting to send email from {FromEmail} to {ToEmail} via {Server}", fromEmail, toEmail, server);

            try
            {
                using (var message = new MailMessage())
                {
                    message.From = new MailAddress(fromEmail);
                    message.Subject = subject;
                    message.Body = header + body + footer;
                    message.IsBodyHtml = true;
                    message.To.Add(new MailAddress(toEmail));

                    using (var client = new SmtpClient(server))
                    {
                        client.Port = 587;
                        client.Credentials = new NetworkCredential(fromEmail, password);
                        client.EnableSsl = true;
                        client.Timeout = 30000; // 30 seconds timeout

                        client.Send(message);
                        _logger.LogInformation("Email successfully sent to {ToEmail}", toEmail);
                        return true;
                    }
                }
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
