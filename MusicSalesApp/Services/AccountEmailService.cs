using System.Text;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for sending account-related confirmation emails.
/// </summary>
public class AccountEmailService : IAccountEmailService
{
    private readonly IEmailService _emailService;
    private readonly ILogger<AccountEmailService> _logger;

    public AccountEmailService(
        IEmailService emailService,
        ILogger<AccountEmailService> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> SendAccountCreatedEmailAsync(string userEmail, string userName, string baseUrl)
    {
        try
        {
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";

            var body = new StringBuilder();
            body.Append(BuildEmailHeader(logoUrl, "Welcome to StreamTunes!"));
            body.Append(BuildGreeting(userName));
            body.Append(@"
                <p style='font-size: 16px; color: #333;'>Thank you for creating an account with StreamTunes!</p>
                <p style='font-size: 16px; color: #333;'>Your account has been successfully created. You can now:</p>
                <ul style='font-size: 16px; color: #333;'>
                    <li>Browse and purchase your favorite music</li>
                    <li>Create personalized playlists</li>
                    <li>Subscribe for unlimited streaming access</li>
                </ul>
                <p style='font-size: 16px; color: #333;'>If you did not create this account, please contact our support team immediately.</p>
            ");
            body.Append(BuildEmailFooter());

            var subject = "StreamTunes - Welcome to Your New Account";
            return await _emailService.SendEmailAsync(userEmail, subject, body.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending account created email to {Email}", userEmail);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendAccountClosedEmailAsync(string userEmail, string userName, string baseUrl)
    {
        try
        {
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";

            var body = new StringBuilder();
            body.Append(BuildEmailHeader(logoUrl, "Account Suspended"));
            body.Append(BuildGreeting(userName));
            body.Append(@"
                <p style='font-size: 16px; color: #333;'>Your StreamTunes account has been suspended as requested.</p>
                <p style='font-size: 16px; color: #333;'>While your account is suspended:</p>
                <ul style='font-size: 16px; color: #333;'>
                    <li>You will not be able to log in</li>
                    <li>You will not receive any communications from us</li>
                    <li>Your purchased music will remain safe and accessible if you reactivate your account</li>
                </ul>
                <p style='font-size: 16px; color: #333;'>If you wish to reactivate your account in the future, please contact our support team.</p>
                <p style='font-size: 16px; color: #333;'>If you did not request this action, please contact us immediately.</p>
            ");
            body.Append(BuildEmailFooter());

            var subject = "StreamTunes - Account Suspended";
            return await _emailService.SendEmailAsync(userEmail, subject, body.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending account closed email to {Email}", userEmail);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendPasswordChangedEmailAsync(string userEmail, string userName, string baseUrl)
    {
        try
        {
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";

            var body = new StringBuilder();
            body.Append(BuildEmailHeader(logoUrl, "Password Changed"));
            body.Append(BuildGreeting(userName));
            body.Append($@"
                <p style='font-size: 16px; color: #333;'>Your StreamTunes account password has been successfully changed.</p>
                <p style='font-size: 16px; color: #333;'>This change was made on {DateTime.UtcNow:MMMM dd, yyyy 'at' h:mm tt} UTC.</p>
                <p style='font-size: 16px; color: #333;'><strong>If you did not make this change</strong>, please take the following steps immediately:</p>
                <ol style='font-size: 16px; color: #333;'>
                    <li>Reset your password using the 'Forgot Password' link on the login page</li>
                    <li>Contact our support team to report unauthorized access</li>
                </ol>
                <p style='font-size: 16px; color: #333;'>For your security, we recommend using a strong, unique password for your account.</p>
            ");
            body.Append(BuildEmailFooter());

            var subject = "StreamTunes - Password Changed";
            return await _emailService.SendEmailAsync(userEmail, subject, body.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending password changed email to {Email}", userEmail);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendAccountDeletedEmailAsync(string userEmail, string userName, string baseUrl)
    {
        try
        {
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";

            var body = new StringBuilder();
            body.Append(BuildEmailHeader(logoUrl, "Account Deleted"));
            body.Append(BuildGreeting(userName));
            body.Append(@"
                <p style='font-size: 16px; color: #333;'>Your StreamTunes account has been permanently deleted as requested.</p>
                <p style='font-size: 16px; color: #333;'>All your data, including:</p>
                <ul style='font-size: 16px; color: #333;'>
                    <li>Purchase history</li>
                    <li>Playlists</li>
                    <li>Subscriptions</li>
                    <li>Account preferences</li>
                </ul>
                <p style='font-size: 16px; color: #333;'>has been permanently removed from our systems.</p>
                <p style='font-size: 16px; color: #333;'>We're sorry to see you go. If you ever decide to return, you're always welcome to create a new account.</p>
                <p style='font-size: 16px; color: #333;'>Thank you for being a part of StreamTunes.</p>
            ");
            body.Append(BuildEmailFooter());

            var subject = "StreamTunes - Account Deleted";
            return await _emailService.SendEmailAsync(userEmail, subject, body.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending account deleted email to {Email}", userEmail);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendSubscriptionCancelledEmailAsync(string userEmail, string userName, DateTime? endDate, string baseUrl)
    {
        try
        {
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";
            var endDateDisplay = endDate?.ToString("MMMM dd, yyyy 'at' h:mm tt") ?? "the end of your billing period";

            var body = new StringBuilder();
            body.Append(BuildEmailHeader(logoUrl, "Subscription Cancelled"));
            body.Append(BuildGreeting(userName));
            body.Append($@"
                <p style='font-size: 16px; color: #333;'>Your StreamTunes subscription has been cancelled as requested.</p>
                <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                    <h3 style='margin: 0 0 10px 0; color: #333; font-size: 18px;'>Important Information</h3>
                    <p style='font-size: 16px; color: #333; margin: 0;'>Your subscription will remain active until: <strong>{endDateDisplay}</strong></p>
                </div>
                <p style='font-size: 16px; color: #333;'>Until then, you can continue to enjoy:</p>
                <ul style='font-size: 16px; color: #333;'>
                    <li>Unlimited music streaming</li>
                    <li>Access to all your playlists</li>
                    <li>All premium features</li>
                </ul>
                <p style='font-size: 16px; color: #333;'>After your subscription ends, you will still have access to any music you have purchased.</p>
                <p style='font-size: 16px; color: #333;'>If you change your mind, you can resubscribe at any time from your account settings.</p>
            ");
            body.Append(BuildEmailFooter());

            var subject = "StreamTunes - Subscription Cancelled";
            return await _emailService.SendEmailAsync(userEmail, subject, body.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending subscription cancelled email to {Email}", userEmail);
            return false;
        }
    }

    private string BuildEmailHeader(string logoUrl, string title)
    {
        return $@"
        <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
            <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>{title}</h1>
            </div>
            <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
        ";
    }

    private string BuildGreeting(string userName)
    {
        var displayName = string.IsNullOrEmpty(userName) ? "Valued Customer" : userName;
        return $"<p style='font-size: 16px; color: #333;'>Hello {System.Web.HttpUtility.HtmlEncode(displayName)},</p>";
    }

    private string BuildEmailFooter()
    {
        return $@"
                <div style='margin-top: 30px; padding-top: 20px; border-top: 1px solid #e0e0e0; text-align: center;'>
                    <p style='color: #666; font-size: 14px;'>Thank you for choosing StreamTunes!</p>
                    <p style='color: #999; font-size: 12px;'>If you have any questions, please contact our support team.</p>
                </div>
            </div>
        </div>
        ";
    }
}
