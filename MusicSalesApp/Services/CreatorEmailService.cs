#nullable enable
namespace MusicSalesApp.Services;

/// <summary>
/// Service for sending emails related to creator operations.
/// Handles tax form (W-9/W-8) webhook notifications and creator onboarding communications.
/// </summary>
public class CreatorEmailService : ICreatorEmailService
{
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CreatorEmailService> _logger;
    private readonly string _adminEmail;
    private readonly string _customerServiceEmail;

    public CreatorEmailService(
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<CreatorEmailService> logger)
    {
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
        _adminEmail = configuration["EmailSettings:AdminEmail"] ?? "admin@streamtunes.net";
        _customerServiceEmail = configuration["EmailSettings:CustomerServiceEmail"] ?? "customerservice@streamtunes.net";
    }

    /// <inheritdoc />
    public async Task<bool> SendTaxFormReceivedEmailAsync(string userEmail, string baseUrl, string formType)
    {
        _logger.LogInformation("Sending tax form received email to {Email} for {FormType}", userEmail, formType);

        try
        {
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";
            var manageAccountUrl = $"{baseUrl.TrimEnd('/')}/manage-account";

            var subject = "Tax Form Received - Under Review";
            var body = $@"
                <div style='text-align: center; margin-bottom: 20px;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                </div>
                <h2>Tax Form Received</h2>
                <p>Thank you for submitting your {formType} tax form!</p>
                <p>We have received your submission and are currently analyzing the information. 
                   This process typically takes a few moments.</p>
                <p>You will receive another email shortly to let you know the outcome of your submission.</p>
                <p>If you have any questions in the meantime, please contact us at 
                   <a href='mailto:{_customerServiceEmail}'>{_customerServiceEmail}</a>.</p>
                <p style='color: #999; font-size: 12px;'>
                    <a href='{manageAccountUrl}' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
                </p>";

            return await _emailService.SendEmailAsync(userEmail, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending tax form received email to {Email}", userEmail);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendTaxFormProcessingErrorEmailAsync(string userEmail, string baseUrl, string? submissionId, string errorDetails)
    {
        _logger.LogInformation("Sending tax form processing error email to {Email}", userEmail);

        try
        {
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";
            var manageAccountUrl = $"{baseUrl.TrimEnd('/')}/manage-account";

            // Email to the user
            var userSubject = "Issue Processing Your Tax Form";
            var userBody = $@"
                <div style='text-align: center; margin-bottom: 20px;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                </div>
                <h2>Issue Processing Your Tax Form</h2>
                <p>We encountered an issue while processing your tax form submission.</p>
                <p>We sincerely apologize for any inconvenience this may have caused. This error is not necessarily 
                   related to the information you provided.</p>
                <p><strong>What to do next:</strong></p>
                <ol>
                    <li>Go to <a href='{manageAccountUrl}'>Account Management</a></li>
                    <li>Request a new tax form email</li>
                    <li>Complete the W-9 or W-8 form again</li>
                </ol>
                <p>If you continue to experience issues, please contact us at 
                   <a href='mailto:{_customerServiceEmail}'>{_customerServiceEmail}</a>.</p>
                <p style='color: #999; font-size: 12px;'>
                    <a href='{manageAccountUrl}' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
                </p>";

            var userEmailSent = await _emailService.SendEmailAsync(userEmail, userSubject, userBody);

            // Email to admin with error details (DO NOT include sensitive information)
            var adminSubject = "Tax Form Processing Error - Action Required";
            var adminBody = $@"
                <div style='text-align: center; margin-bottom: 20px;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                </div>
                <h2>Tax Form Processing Error</h2>
                <p>An error occurred while processing a tax form webhook response.</p>
                <p><strong>User Email:</strong> {userEmail}</p>
                <p><strong>Submission ID:</strong> {submissionId ?? "N/A"}</p>
                <p><strong>Error Details:</strong></p>
                <pre style='background: #f5f5f5; padding: 10px; overflow-x: auto; max-width: 600px;'>{System.Web.HttpUtility.HtmlEncode(errorDetails)}</pre>
                <p>Please investigate this error in TaxBandits using the Submission ID above.</p>";

            var adminEmailSent = await _emailService.SendEmailAsync(_adminEmail, adminSubject, adminBody);

            return userEmailSent && adminEmailSent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending tax form processing error email to {Email}", userEmail);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendTaxFormFailedEmailAsync(string userEmail, string baseUrl, string formType, string? failureReason = null)
    {
        _logger.LogInformation("Sending tax form failed email to {Email} for {FormType}", userEmail, formType);

        try
        {
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";
            var manageAccountUrl = $"{baseUrl.TrimEnd('/')}/manage-account";

            var reasonText = !string.IsNullOrWhiteSpace(failureReason)
                ? $"<p><strong>Reason:</strong> {System.Web.HttpUtility.HtmlEncode(failureReason)}</p>"
                : "";

            var subject = $"{formType} Form Submission Failed";
            var body = $@"
                <div style='text-align: center; margin-bottom: 20px;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                </div>
                <h2>{formType} Form Submission Failed</h2>
                <p>Unfortunately, your {formType} tax form submission was not successful.</p>
                {reasonText}
                <p>This could be due to a TIN verification issue or other validation problem with the information provided.</p>
                <p><strong>What to do next:</strong></p>
                <ol>
                    <li>Go to <a href='{manageAccountUrl}'>Account Management</a></li>
                    <li>Request a new tax form email</li>
                    <li>Complete the {formType} form again</li>
                    <li>Double-check all the information you provide, especially your Tax Identification Number (TIN)</li>
                </ol>
                <p>If you continue to experience issues, please contact us at 
                   <a href='mailto:{_customerServiceEmail}'>{_customerServiceEmail}</a>.</p>
                <p style='color: #999; font-size: 12px;'>
                    <a href='{manageAccountUrl}' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
                </p>";

            return await _emailService.SendEmailAsync(userEmail, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending tax form failed email to {Email}", userEmail);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendTaxFormSuccessEmailAsync(string userEmail, string baseUrl, string formType, string? countryCode = null)
    {
        _logger.LogInformation("Sending tax form success email to {Email} for {FormType}", userEmail, formType);

        try
        {
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";
            var manageAccountUrl = $"{baseUrl.TrimEnd('/')}/manage-account";

            // Email to user
            var userSubject = "Welcome to StreamTunes - Tax Form Approved!";
            var userBody = $@"
                <div style='text-align: center; margin-bottom: 20px;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                </div>
                <h2>Welcome to StreamTunes!</h2>
                <p>Congratulations! Your {formType} tax form has been successfully processed and approved.</p>
                <p>You are now ready to start sharing your music with the world!</p>
                <h3>Next Steps:</h3>
                <ol>
                    <li>Go to <a href='{manageAccountUrl}'>Account Management</a></li>
                    <li>Upload your music files and album art</li>
                    <li>Start earning from your creations!</li>
                </ol>
                <p>If you have any questions or need help getting started, please don't hesitate to contact us at 
                   <a href='mailto:{_customerServiceEmail}'>{_customerServiceEmail}</a>.</p>
                <p>We're excited to have you as part of the StreamTunes creator community!</p>
                <p style='color: #999; font-size: 12px;'>
                    <a href='{manageAccountUrl}' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
                </p>";

            var userEmailSent = await _emailService.SendEmailAsync(userEmail, userSubject, userBody);

            // Email to admin
            var countryName = !string.IsNullOrWhiteSpace(countryCode) 
                ? GetCountryName(countryCode) 
                : null;

            var countryInfo = formType == "W-8" && !string.IsNullOrWhiteSpace(countryName)
                ? $"<p><strong>Country:</strong> {countryName}</p>"
                : "";

            var adminSubject = "New Creator Tax Form Completed";
            var adminBody = $@"
                <div style='text-align: center; margin-bottom: 20px;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                </div>
                <h2>New Creator Tax Form Completed</h2>
                <p>A new creator has successfully completed their tax form onboarding.</p>
                <p><strong>User Email:</strong> {userEmail}</p>
                <p><strong>Form Type:</strong> {formType}</p>
                {countryInfo}";

            var adminEmailSent = await _emailService.SendEmailAsync(_adminEmail, adminSubject, adminBody);

            return userEmailSent && adminEmailSent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending tax form success email to {Email}", userEmail);
            return false;
        }
    }

    /// <summary>
    /// Converts an ISO-2 country code to the full country name.
    /// </summary>
    private static string GetCountryName(string iso2Code)
    {
        if (string.IsNullOrWhiteSpace(iso2Code))
            return iso2Code;

        // Comprehensive country mappings (reverse of TaxBanditsController mapping)
        var countryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // A
            { "AF", "Afghanistan" },
            { "AL", "Albania" },
            { "DZ", "Algeria" },
            { "AD", "Andorra" },
            { "AO", "Angola" },
            { "AG", "Antigua and Barbuda" },
            { "AR", "Argentina" },
            { "AM", "Armenia" },
            { "AU", "Australia" },
            { "AT", "Austria" },
            { "AZ", "Azerbaijan" },
            // B
            { "BS", "Bahamas" },
            { "BH", "Bahrain" },
            { "BD", "Bangladesh" },
            { "BB", "Barbados" },
            { "BY", "Belarus" },
            { "BE", "Belgium" },
            { "BZ", "Belize" },
            { "BJ", "Benin" },
            { "BT", "Bhutan" },
            { "BO", "Bolivia" },
            { "BA", "Bosnia and Herzegovina" },
            { "BW", "Botswana" },
            { "BR", "Brazil" },
            { "BN", "Brunei" },
            { "BG", "Bulgaria" },
            { "BF", "Burkina Faso" },
            { "BI", "Burundi" },
            // C
            { "KH", "Cambodia" },
            { "CM", "Cameroon" },
            { "CA", "Canada" },
            { "CV", "Cape Verde" },
            { "CF", "Central African Republic" },
            { "TD", "Chad" },
            { "CL", "Chile" },
            { "CN", "China" },
            { "CO", "Colombia" },
            { "KM", "Comoros" },
            { "CG", "Congo" },
            { "CR", "Costa Rica" },
            { "HR", "Croatia" },
            { "CU", "Cuba" },
            { "CY", "Cyprus" },
            { "CZ", "Czech Republic" },
            // D
            { "DK", "Denmark" },
            { "DJ", "Djibouti" },
            { "DM", "Dominica" },
            { "DO", "Dominican Republic" },
            // E
            { "EC", "Ecuador" },
            { "EG", "Egypt" },
            { "SV", "El Salvador" },
            { "GQ", "Equatorial Guinea" },
            { "ER", "Eritrea" },
            { "EE", "Estonia" },
            { "SZ", "Eswatini" },
            { "ET", "Ethiopia" },
            // F
            { "FJ", "Fiji" },
            { "FI", "Finland" },
            { "FR", "France" },
            // G
            { "GA", "Gabon" },
            { "GM", "Gambia" },
            { "GE", "Georgia" },
            { "DE", "Germany" },
            { "GH", "Ghana" },
            { "GR", "Greece" },
            { "GD", "Grenada" },
            { "GT", "Guatemala" },
            { "GN", "Guinea" },
            { "GW", "Guinea-Bissau" },
            { "GY", "Guyana" },
            // H
            { "HT", "Haiti" },
            { "HN", "Honduras" },
            { "HK", "Hong Kong" },
            { "HU", "Hungary" },
            // I
            { "IS", "Iceland" },
            { "IN", "India" },
            { "ID", "Indonesia" },
            { "IR", "Iran" },
            { "IQ", "Iraq" },
            { "IE", "Ireland" },
            { "IL", "Israel" },
            { "IT", "Italy" },
            { "CI", "Ivory Coast" },
            // J
            { "JM", "Jamaica" },
            { "JP", "Japan" },
            { "JO", "Jordan" },
            // K
            { "KZ", "Kazakhstan" },
            { "KE", "Kenya" },
            { "KI", "Kiribati" },
            { "KR", "South Korea" },
            { "KP", "North Korea" },
            { "KW", "Kuwait" },
            { "KG", "Kyrgyzstan" },
            // L
            { "LA", "Laos" },
            { "LV", "Latvia" },
            { "LB", "Lebanon" },
            { "LS", "Lesotho" },
            { "LR", "Liberia" },
            { "LY", "Libya" },
            { "LI", "Liechtenstein" },
            { "LT", "Lithuania" },
            { "LU", "Luxembourg" },
            // M
            { "MG", "Madagascar" },
            { "MW", "Malawi" },
            { "MY", "Malaysia" },
            { "MV", "Maldives" },
            { "ML", "Mali" },
            { "MT", "Malta" },
            { "MH", "Marshall Islands" },
            { "MR", "Mauritania" },
            { "MU", "Mauritius" },
            { "MX", "Mexico" },
            { "FM", "Micronesia" },
            { "MD", "Moldova" },
            { "MC", "Monaco" },
            { "MN", "Mongolia" },
            { "ME", "Montenegro" },
            { "MA", "Morocco" },
            { "MZ", "Mozambique" },
            { "MM", "Myanmar" },
            // N
            { "NA", "Namibia" },
            { "NR", "Nauru" },
            { "NP", "Nepal" },
            { "NL", "Netherlands" },
            { "NZ", "New Zealand" },
            { "NI", "Nicaragua" },
            { "NE", "Niger" },
            { "NG", "Nigeria" },
            { "NO", "Norway" },
            // O
            { "OM", "Oman" },
            // P
            { "PK", "Pakistan" },
            { "PW", "Palau" },
            { "PS", "Palestine" },
            { "PA", "Panama" },
            { "PG", "Papua New Guinea" },
            { "PY", "Paraguay" },
            { "PE", "Peru" },
            { "PH", "Philippines" },
            { "PL", "Poland" },
            { "PT", "Portugal" },
            // Q
            { "QA", "Qatar" },
            // R
            { "RO", "Romania" },
            { "RU", "Russia" },
            { "RW", "Rwanda" },
            // S
            { "KN", "Saint Kitts and Nevis" },
            { "LC", "Saint Lucia" },
            { "VC", "Saint Vincent and the Grenadines" },
            { "WS", "Samoa" },
            { "SM", "San Marino" },
            { "SA", "Saudi Arabia" },
            { "SN", "Senegal" },
            { "RS", "Serbia" },
            { "SC", "Seychelles" },
            { "SL", "Sierra Leone" },
            { "SG", "Singapore" },
            { "SK", "Slovakia" },
            { "SI", "Slovenia" },
            { "SB", "Solomon Islands" },
            { "SO", "Somalia" },
            { "ZA", "South Africa" },
            { "SS", "South Sudan" },
            { "ES", "Spain" },
            { "LK", "Sri Lanka" },
            { "SD", "Sudan" },
            { "SR", "Suriname" },
            { "SE", "Sweden" },
            { "CH", "Switzerland" },
            { "SY", "Syria" },
            // T
            { "TW", "Taiwan" },
            { "TJ", "Tajikistan" },
            { "TZ", "Tanzania" },
            { "TH", "Thailand" },
            { "TL", "Timor-Leste" },
            { "TG", "Togo" },
            { "TO", "Tonga" },
            { "TT", "Trinidad and Tobago" },
            { "TN", "Tunisia" },
            { "TR", "Turkey" },
            { "TM", "Turkmenistan" },
            { "TV", "Tuvalu" },
            // U
            { "UG", "Uganda" },
            { "UA", "Ukraine" },
            { "AE", "United Arab Emirates" },
            { "GB", "United Kingdom" },
            { "US", "United States" },
            { "UY", "Uruguay" },
            { "UZ", "Uzbekistan" },
            // V
            { "VU", "Vanuatu" },
            { "VA", "Vatican City" },
            { "VE", "Venezuela" },
            { "VN", "Vietnam" },
            // Y
            { "YE", "Yemen" },
            // Z
            { "ZM", "Zambia" },
            { "ZW", "Zimbabwe" }
        };

        if (countryMap.TryGetValue(iso2Code, out var countryName))
            return countryName;

        // Return the code if we don't have a mapping
        return iso2Code;
    }
}
