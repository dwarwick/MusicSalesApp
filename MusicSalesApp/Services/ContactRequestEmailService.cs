#nullable enable

using System.Net;

namespace MusicSalesApp.Services;

public class ContactRequestEmailService : IContactRequestEmailService
{
    private const string DefaultAdminEmail = "admin@streamtunes.net";
    private const string DefaultCustomerServiceEmail = "admin@streamtunes.net";

    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ContactRequestEmailService> _logger;

    public ContactRequestEmailService(
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<ContactRequestEmailService> logger)
    {
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ContactRequestEmailResult> SendContactRequestEmailsAsync(string userEmail, string subject, string message)
    {
        var customerServiceEmail = GetCustomerServiceEmail();
        var adminEmail = GetAdminEmail();
        var userSubject = "StreamTunes - We Received Your Message";
        var adminSubject = $"StreamTunes Admin - Contact Form: {subject}";

        var userEmailSent = false;
        var adminEmailSent = false;

        try
        {
            userEmailSent = await _emailService.SendEmailAsync(
                userEmail,
                userSubject,
                BuildUserEmailBody(customerServiceEmail, subject, message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send contact receipt email to {UserEmail}.", userEmail);
        }

        try
        {
            adminEmailSent = await _emailService.SendEmailAsync(
                adminEmail,
                adminSubject,
                BuildAdminEmailBody(userEmail, subject, message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send contact admin email for user {UserEmail}.", userEmail);
        }

        return new ContactRequestEmailResult(userEmailSent, adminEmailSent);
    }

    private string BuildUserEmailBody(string customerServiceEmail, string subject, string message)
    {
        var encodedCustomerServiceEmail = WebUtility.HtmlEncode(customerServiceEmail);
        var encodedSubject = WebUtility.HtmlEncode(subject);
        var encodedMessage = FormatMessageForEmail(message);
        var logoHtml = _emailService.GetEmailLogoHtml();

        return $@"
{logoHtml}
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
    <h2 style='color: #333;'>Thanks for contacting StreamTunes</h2>
    <p style='font-size: 16px; color: #333;'>We received your message and will respond as soon as we can, usually within 48 hours.</p>
    <p style='font-size: 16px; color: #333;'>You can also reach customer service at <a href='mailto:{encodedCustomerServiceEmail}'>{encodedCustomerServiceEmail}</a>.</p>
    <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
        <p style='font-size: 14px; color: #333; margin: 0 0 10px 0;'><strong>Subject:</strong> {encodedSubject}</p>
        <p style='font-size: 14px; color: #333; margin: 0 0 10px 0;'><strong>Your message:</strong></p>
        <div style='font-size: 14px; color: #333; white-space: normal;'>{encodedMessage}</div>
    </div>
</div>";
    }

    private string BuildAdminEmailBody(string userEmail, string subject, string message)
    {
        var encodedUserEmail = WebUtility.HtmlEncode(userEmail);
        var encodedSubject = WebUtility.HtmlEncode(subject);
        var encodedMessage = FormatMessageForEmail(message);
        var logoHtml = _emailService.GetEmailLogoHtml();

        return $@"
{logoHtml}
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
    <h2 style='color: #333;'>Mobile Contact Form Submission</h2>
    <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
        <p style='font-size: 14px; color: #333; margin: 0 0 10px 0;'><strong>User Email:</strong> {encodedUserEmail}</p>
        <p style='font-size: 14px; color: #333; margin: 0 0 10px 0;'><strong>Subject:</strong> {encodedSubject}</p>
        <p style='font-size: 14px; color: #333; margin: 0 0 10px 0;'><strong>Message:</strong></p>
        <div style='font-size: 14px; color: #333; white-space: normal;'>{encodedMessage}</div>
    </div>
</div>";
    }

    private string GetCustomerServiceEmail() =>
        _configuration["EmailSettings:CustomerServiceEmail"] ?? DefaultCustomerServiceEmail;

    private string GetAdminEmail() =>
        _configuration["EmailSettings:AdminEmail"] ?? DefaultAdminEmail;

    private static string FormatMessageForEmail(string message)
    {
        var encoded = WebUtility.HtmlEncode(message.Trim());
        return encoded.Replace("\r\n", "<br />").Replace("\n", "<br />");
    }
}