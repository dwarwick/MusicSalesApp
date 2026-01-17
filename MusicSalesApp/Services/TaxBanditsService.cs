#nullable enable
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for TaxBandits OAuth authentication and W-9/W-8 tax form operations.
/// </summary>
public sealed class TaxBanditsService : ITaxBanditsService
{
    private readonly HttpClient _http;
    private readonly ILogger<TaxBanditsService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IEmailService _emailService;
    private readonly string _adminEmail;

    public TaxBanditsService(
        HttpClient http, 
        ILogger<TaxBanditsService> logger, 
        IConfiguration configuration,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IEmailService emailService)
    {
        _http = http;
        _logger = logger;
        _configuration = configuration;
        _dbContextFactory = dbContextFactory;
        _emailService = emailService;
        _adminEmail = configuration["EmailSettings:CustomerServiceEmail"] ?? "admin@streamtunes.net";
    }

    /// <inheritdoc />
    public async Task<TaxBanditsAuthResponse> GetAccessTokenAsync(
        string clientId,
        string userToken,
        string clientSecret,
        bool useSandbox = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("clientId is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(userToken)) throw new ArgumentException("userToken is required.", nameof(userToken));
        if (string.IsNullOrWhiteSpace(clientSecret)) throw new ArgumentException("clientSecret is required.", nameof(clientSecret));

        _logger.LogInformation("Requesting TaxBandits access token for clientId: {ClientId}", clientId);

        // 1) Build JWS (HS256)
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var jws = CreateJwsHs256(clientId, userToken, clientSecret, iat);

        // 2) Get URL from configuration or use defaults
        var url = useSandbox
            ? _configuration["TaxBandits:SandboxAuthUrl"] ?? "https://testoauth.expressauth.net/v2/tbsauth"
            : _configuration["TaxBandits:ProductionAuthUrl"] ?? "https://oauth.expressauth.net/v2/tbsauth";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // TaxBandits requires this exact header name:
        req.Headers.TryAddWithoutValidation("Authentication", jws);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Helpful error detail if status != 200
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("TaxBandits auth failed: {StatusCode} {ReasonPhrase}. Body: {Body}", 
                (int)resp.StatusCode, resp.ReasonPhrase, body);
            throw new HttpRequestException(
                $"TaxBandits auth failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }

        var result = JsonSerializer.Deserialize<TaxBanditsAuthResponse>(body, JsonOptions)
                     ?? throw new InvalidOperationException("Failed to deserialize TaxBandits auth response.");

        _logger.LogInformation("TaxBandits access token obtained successfully");
        return result;
    }

    /// <inheritdoc />
    public async Task<W9RequestResponse> RequestW9ByEmailAsync(
        int userId,
        string email,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("email is required.", nameof(email));

        _logger.LogInformation("Requesting W-9 for user {UserId} at email {Email}", userId, email);

        var response = new W9RequestResponse();
        
        try
        {
            // Get configuration values
            var clientId = _configuration["TaxBandits:ClientId"];
            var clientSecret = _configuration["TaxBandits:ClientSecret"];
            var userToken = _configuration["TaxBandits:UserToken"];
            var businessId = _configuration["TaxBandits:BusinessId"];
            var webhookRef = _configuration["TaxBandits:WebhookRef"];
            var emailCustomizationId = _configuration["TaxBandits:EmailCustomizationId"];
            var customizationId = _configuration["TaxBandits:CustomizationId"];

            var useSandbox = _configuration.GetValue<bool>("TaxBandits:UseSandbox", true);

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || 
                string.IsNullOrWhiteSpace(userToken) || string.IsNullOrWhiteSpace(businessId))
            {
                var errorMsg = "TaxBandits configuration is incomplete. Please check ClientId, ClientSecret, UserToken, and BusinessId.";
                _logger.LogError(errorMsg);
                response.Success = false;
                response.ErrorMessage = errorMsg;
                await SendErrorEmailsAsync(email, baseUrl, errorMsg, null);
                return response;
            }

            // First, send notification email to user about incoming TaxBandits email
            await SendW9NotificationEmailAsync(email, baseUrl);

            // Get access token
            var authResponse = await GetAccessTokenAsync(clientId, userToken, clientSecret, useSandbox, cancellationToken);
            
            if (string.IsNullOrWhiteSpace(authResponse.AccessToken))
            {
                var errorMsg = "Failed to obtain TaxBandits access token.";
                _logger.LogError(errorMsg);
                response.Success = false;
                response.ErrorMessage = errorMsg;
                await SendErrorEmailsAsync(email, baseUrl, errorMsg, null);
                await SaveW9RequestAsync(userId, email, null, "AUTH_FAILED", errorMsg, null, cancellationToken);
                return response;
            }

            // Build the API request
            var apiUrl = useSandbox
                ? _configuration["TaxBandits:SandboxApiUrl"] ?? "https://testapi.taxbandits.com/v1.7.3/"
                : _configuration["TaxBandits:ProductionApiUrl"] ?? "https://api.taxbandits.com/";

            var requestBody = new
            {
                SubmissionManifest = new
                {
                    IsTINMatching = true
                },
                Requester = new
                {
                    BusinessId = businessId
                },
                EmailCustomizationId = emailCustomizationId,
                CustomizationId = customizationId,
                Recipients = new[]
                {
                    new
                    {
                        PayeeRef = email,
                        Email = email
                    }
                },
                WebhookRef = webhookRef
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl.TrimEnd('/')}/WhCertificate/RequestByEmail");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResponse.AccessToken);
            req.Content = content;

            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var responseBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            response.RawResponse = responseBody;

            if (resp.IsSuccessStatusCode)
            {
                // Parse the success response
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                response.SubmissionId = root.TryGetProperty("SubmissionId", out var submissionIdElement) 
                    ? submissionIdElement.GetString() 
                    : null;

                // Check for success records
                if (root.TryGetProperty("WhCertificate", out var whCertificate) && 
                    whCertificate.TryGetProperty("SuccessRecords", out var successRecords) &&
                    successRecords.ValueKind == JsonValueKind.Array &&
                    successRecords.GetArrayLength() > 0)
                {
                    var firstRecord = successRecords[0];
                    response.Status = firstRecord.TryGetProperty("WhCertificateStatus", out var statusElement) 
                        ? statusElement.GetString() 
                        : "ORDER_CREATED";
                    response.Success = true;

                    _logger.LogInformation("W-9 request successful for user {UserId}. SubmissionId: {SubmissionId}, Status: {Status}", 
                        userId, response.SubmissionId, response.Status);

                    await SaveW9RequestAsync(userId, email, response.SubmissionId, response.Status, null, responseBody, cancellationToken);
                }
                // Check for error records in the response
                else if (root.TryGetProperty("WhCertificate", out var whCertErr) && 
                         whCertErr.TryGetProperty("ErrorRecords", out var errorRecords) &&
                         errorRecords.ValueKind == JsonValueKind.Array &&
                         errorRecords.GetArrayLength() > 0)
                {
                    var firstError = errorRecords[0];
                    var errorMsg = firstError.TryGetProperty("Errors", out var errors) && 
                                   errors.ValueKind == JsonValueKind.Array && 
                                   errors.GetArrayLength() > 0 &&
                                   errors[0].TryGetProperty("Message", out var msgElement)
                        ? msgElement.GetString() ?? "Unknown error from TaxBandits"
                        : "Unknown error from TaxBandits";

                    response.Success = false;
                    response.ErrorMessage = errorMsg;
                    
                    _logger.LogError("W-9 request failed for user {UserId}: {ErrorMessage}", userId, errorMsg);
                    await SendErrorEmailsAsync(email, baseUrl, errorMsg, responseBody);
                    await SaveW9RequestAsync(userId, email, response.SubmissionId, "ERROR", errorMsg, responseBody, cancellationToken);
                }
                // Check for top-level errors
                else if (root.TryGetProperty("Errors", out var topLevelErrors) &&
                         topLevelErrors.ValueKind == JsonValueKind.Array &&
                         topLevelErrors.GetArrayLength() > 0)
                {
                    var firstError = topLevelErrors[0];
                    var errorId = firstError.TryGetProperty("Id", out var idElement) ? idElement.GetString() : null;
                    var errorMsg = firstError.TryGetProperty("Message", out var msgElement) ? msgElement.GetString() ?? "Unknown error" : "Unknown error";

                    response.Success = false;
                    response.ErrorMessage = errorMsg;

                    _logger.LogError("W-9 request failed for user {UserId}. ErrorId: {ErrorId}, Message: {ErrorMessage}", 
                        userId, errorId, errorMsg);
                    await SendErrorEmailsAsync(email, baseUrl, errorMsg, responseBody);
                    await SaveW9RequestAsync(userId, email, null, "ERROR", errorMsg, responseBody, cancellationToken, errorId);
                }
                else
                {
                    // Unexpected response format but HTTP was successful
                    response.Success = true;
                    response.Status = "UNKNOWN";
                    await SaveW9RequestAsync(userId, email, response.SubmissionId, "UNKNOWN", null, responseBody, cancellationToken);
                }
            }
            else
            {
                // HTTP error response
                var errorMsg = $"TaxBandits API returned HTTP {(int)resp.StatusCode}";
                
                // Try to extract error details from response
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("Errors", out var errors) &&
                        errors.ValueKind == JsonValueKind.Array &&
                        errors.GetArrayLength() > 0)
                    {
                        var firstError = errors[0];
                        if (firstError.TryGetProperty("Message", out var msgElement))
                        {
                            errorMsg = msgElement.GetString() ?? errorMsg;
                        }
                    }
                    else if (root.TryGetProperty("StatusMessage", out var statusMsgElement))
                    {
                        errorMsg = statusMsgElement.GetString() ?? errorMsg;
                    }
                }
                catch (JsonException)
                {
                    // Couldn't parse error response, use default message
                }

                response.Success = false;
                response.ErrorMessage = errorMsg;

                _logger.LogError("W-9 request failed for user {UserId}. HTTP {StatusCode}: {ErrorMessage}", 
                    userId, (int)resp.StatusCode, errorMsg);
                await SendErrorEmailsAsync(email, baseUrl, errorMsg, responseBody);
                await SaveW9RequestAsync(userId, email, null, "HTTP_ERROR", errorMsg, responseBody, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while requesting W-9 for user {UserId}", userId);
            response.Success = false;
            response.ErrorMessage = ex.Message;
            await SendErrorEmailsAsync(email, baseUrl, ex.Message, null);
            await SaveW9RequestAsync(userId, email, null, "EXCEPTION", ex.Message, null, cancellationToken);
        }

        return response;
    }

    private async Task SendW9NotificationEmailAsync(string email, string baseUrl)
    {
        var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";
        var subject = "Action Required: Complete Your Tax Form";
        var body = $@"
            <div style='text-align: center; margin-bottom: 20px;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
            </div>
            <h2>Tax Form Required</h2>
            <p>Thank you for joining StreamTunes as a creator!</p>
            <p>As part of the onboarding process, you need to complete your W-9 or W-8 tax form. 
               You will receive an email shortly from <strong>support.streamtunes@apptaxbandits.com</strong> with a secure link to complete your tax form.
            The subject of the email will be <em>Form W9/W8 Request from David Russell Warwick, DBA Streamtunes</em>.</p>
               Please check your inbox (and spam/junk folder) for this email.</p>
            <p><strong>Important:</strong> Please look for an email from TaxBandits and complete the form as soon as possible 
               to finalize your creator account setup.</p>
            <p>If you have any questions, please contact us at <a href='mailto:{_adminEmail}'>{_adminEmail}</a>.</p>
            <p style='color: #999; font-size: 12px;'>
                <a href='{baseUrl.TrimEnd('/')}/manage-account' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
            </p>";

        await _emailService.SendEmailAsync(email, subject, body);
        _logger.LogInformation("Sent W-9 notification email to {Email}", email);
    }

    private async Task SendErrorEmailsAsync(string userEmail, string baseUrl, string errorMessage, string? rawResponse)
    {
        var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";

        // Truncate raw response to avoid exposing too much sensitive information in emails
        var truncatedResponse = rawResponse != null && rawResponse.Length > 500 
            ? rawResponse.Substring(0, 500) + "... [truncated]" 
            : rawResponse;

        // Email to admin
        var adminSubject = "W-9 Request Failed - Action Required";
        var adminBody = $@"
            <div style='text-align: center; margin-bottom: 20px;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
            </div>
            <h2>W-9 Request Failed</h2>
            <p>A W-9 request failed for the following user:</p>
            <p><strong>User Email:</strong> {userEmail}</p>
            <p><strong>Error:</strong> {errorMessage}</p>
            {(truncatedResponse != null ? $"<p><strong>Response Summary:</strong></p><pre style='background: #f5f5f5; padding: 10px; overflow-x: auto;'>{System.Web.HttpUtility.HtmlEncode(truncatedResponse)}</pre>" : "")}
            <p>Please investigate and take appropriate action. Full response details are available in the database.</p>";

        await _emailService.SendEmailAsync(_adminEmail, adminSubject, adminBody);
        _logger.LogInformation("Sent W-9 error notification to admin for user {Email}", userEmail);

        // Email to user
        var userSubject = "Issue with Your Tax Form Request";
        var userBody = $@"
            <div style='text-align: center; margin-bottom: 20px;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
            </div>
            <h2>Issue with Your Tax Form Request</h2>
            <p>We encountered an issue while processing your tax form request.</p>
            <p>Our team at <a href='mailto:{_adminEmail}'>{_adminEmail}</a> has been notified and is looking into this issue.</p>
            <p>We apologize for any inconvenience and will reach out to you shortly with next steps.</p>
            <p>If you have any questions, please contact us at <a href='mailto:{_adminEmail}'>{_adminEmail}</a>.</p>
            <p style='color: #999; font-size: 12px;'>
                <a href='{baseUrl.TrimEnd('/')}/manage-account' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
            </p>";

        await _emailService.SendEmailAsync(userEmail, userSubject, userBody);
        _logger.LogInformation("Sent W-9 error notification to user {Email}", userEmail);
    }

    private async Task SaveW9RequestAsync(
        int userId,
        string email,
        string? submissionId,
        string? status,
        string? errorMessage,
        string? rawResponse,
        CancellationToken cancellationToken,
        string? errorId = null)
    {
        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var w9Request = new W9Request
            {
                UserId = userId,
                Email = email,
                SubmissionId = submissionId,
                Status = status,
                ErrorId = errorId,
                ErrorMessage = errorMessage,
                RawResponse = rawResponse,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            context.W9Requests.Add(w9Request);
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Saved W-9 request record for user {UserId}. Status: {Status}", userId, status);
        }
        catch (Exception ex)
        {
            // Log at critical level since audit records are important for compliance
            _logger.LogCritical(ex, "CRITICAL: Failed to save W-9 request record for user {UserId}. Email: {Email}, Status: {Status}. Manual intervention may be required.", 
                userId, email, status);
            // Don't throw - we don't want to fail the main operation if database save fails
            // The critical log will be picked up by monitoring/alerting systems
        }
    }

    /// <summary>
    /// Creates the JWS: base64url(header).base64url(payload).base64url(HMACSHA256(signingInput, clientSecret))
    /// </summary>
    public static string CreateJwsHs256(string clientId, string userToken, string clientSecret, long iatUnixSeconds)
    {
        // Header and payload exactly per TaxBandits doc
        var headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var payloadJson = JsonSerializer.Serialize(new
        {
            iss = clientId,
            sub = clientId,
            aud = userToken,
            iat = iatUnixSeconds
        }, JsonOptions);

        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var signingInput = $"{header}.{payload}";
        var signatureBytes = HmacSha256(Encoding.UTF8.GetBytes(signingInput), Encoding.UTF8.GetBytes(clientSecret));
        var signature = Base64UrlEncode(signatureBytes);

        return $"{signingInput}.{signature}";
    }

    private static byte[] HmacSha256(byte[] data, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        // Base64Url encoding: no padding, + -> -, / -> _
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
