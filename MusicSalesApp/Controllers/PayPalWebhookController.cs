#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MusicSalesApp.Controllers;

/// <summary>
/// API controller for handling PayPal webhook notifications.
/// Handles merchant onboarding events for seller management.
/// </summary>
[Route("api/paypal/webhooks")]
[ApiController]
[AllowAnonymous] // Webhooks come from PayPal, not authenticated users
public class PayPalWebhookController : ControllerBase
{
    private readonly ISellerService _sellerService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PayPalWebhookController> _logger;

    // PayPal webhook event types for merchant onboarding
    private const string MerchantOnboardingCompleted = "MERCHANT.ONBOARDING.COMPLETED";
    private const string MerchantPartnerConsentRevoked = "MERCHANT.PARTNER-CONSENT.REVOKED";

    public PayPalWebhookController(
        ISellerService sellerService,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<PayPalWebhookController> logger)
    {
        _sellerService = sellerService;
        _userManager = userManager;
        _roleManager = roleManager;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Handles incoming PayPal webhook notifications.
    /// See: https://developer.paypal.com/api/rest/webhooks/event-names/#merchant-onboarding
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> HandleWebhook()
    {
        try
        {
            // Enable buffering so the body can be read multiple times if needed
            Request.EnableBuffering();
            
            // Read the raw body for verification
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            
            // Reset the stream position for any subsequent reads
            Request.Body.Position = 0;

            _logger.LogInformation("Received PayPal webhook: {Body}", body);

            // Verify the webhook signature (if webhook ID is configured)
            var webhookId = _configuration["PayPal:WebhookId"];
            if (!string.IsNullOrWhiteSpace(webhookId))
            {
                var isValid = await VerifyWebhookSignatureAsync(body, webhookId);
                if (!isValid)
                {
                    _logger.LogWarning("PayPal webhook signature verification failed");
                    return BadRequest("Invalid webhook signature");
                }
            }
            else
            {
                _logger.LogWarning("PayPal:WebhookId not configured - skipping signature verification");
            }

            // Parse the webhook event
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("event_type", out var eventTypeElement))
            {
                _logger.LogWarning("PayPal webhook missing event_type");
                return BadRequest("Missing event_type");
            }

            var eventType = eventTypeElement.GetString();
            _logger.LogInformation("Processing PayPal webhook event: {EventType}", eventType);

            // Handle different event types
            return eventType switch
            {
                MerchantOnboardingCompleted => await HandleMerchantOnboardingCompletedAsync(root),
                MerchantPartnerConsentRevoked => await HandleMerchantConsentRevokedAsync(root),
                _ => HandleUnknownEvent(eventType)
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse PayPal webhook JSON");
            return BadRequest("Invalid JSON");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing PayPal webhook");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Handles the MERCHANT.ONBOARDING.COMPLETED event.
    /// This is triggered when a seller completes their PayPal onboarding process.
    /// </summary>
    private async Task<IActionResult> HandleMerchantOnboardingCompletedAsync(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("resource", out var resource))
            {
                _logger.LogWarning("MERCHANT.ONBOARDING.COMPLETED webhook missing resource");
                return BadRequest("Missing resource");
            }

            // Extract merchant ID and tracking ID from the webhook
            string? merchantId = null;
            string? trackingId = null;
            bool paymentsReceivable = false;
            bool primaryEmailConfirmed = false;

            if (resource.TryGetProperty("merchant_id", out var merchantIdElement))
            {
                merchantId = merchantIdElement.GetString();
            }

            if (resource.TryGetProperty("tracking_id", out var trackingIdElement))
            {
                trackingId = trackingIdElement.GetString();
            }

            if (resource.TryGetProperty("payments_receivable", out var paymentsReceivableElement))
            {
                paymentsReceivable = paymentsReceivableElement.GetBoolean();
            }

            if (resource.TryGetProperty("primary_email_confirmed", out var emailConfirmedElement))
            {
                primaryEmailConfirmed = emailConfirmedElement.GetBoolean();
            }

            _logger.LogInformation(
                "Processing MERCHANT.ONBOARDING.COMPLETED: MerchantId={MerchantId}, TrackingId={TrackingId}, " +
                "PaymentsReceivable={PaymentsReceivable}, PrimaryEmailConfirmed={PrimaryEmailConfirmed}",
                merchantId, trackingId, paymentsReceivable, primaryEmailConfirmed);

            // Find the seller by tracking ID or merchant ID
            Seller? seller = null;
            if (!string.IsNullOrWhiteSpace(trackingId))
            {
                seller = await _sellerService.GetSellerByTrackingIdAsync(trackingId);
            }
            if (seller == null && !string.IsNullOrWhiteSpace(merchantId))
            {
                seller = await _sellerService.GetSellerByMerchantIdAsync(merchantId);
            }

            if (seller == null)
            {
                _logger.LogWarning(
                    "Could not find seller for MERCHANT.ONBOARDING.COMPLETED webhook: " +
                    "TrackingId={TrackingId}, MerchantId={MerchantId}",
                    trackingId, merchantId);
                // Return 200 OK to prevent PayPal from retrying - the seller may not exist in our system
                return Ok(new { status = "seller_not_found" });
            }

            // Complete the onboarding
            await _sellerService.CompleteOnboardingAsync(
                seller.Id,
                merchantId ?? string.Empty,
                paymentsReceivable,
                primaryEmailConfirmed);

            // Reload seller to get updated status
            seller = await _sellerService.GetSellerByIdAsync(seller.Id);

            // If onboarding is complete, add Seller role to user
            if (seller != null && seller.IsActive)
            {
                // Ensure the Seller role exists
                if (!await _roleManager.RoleExistsAsync(Roles.Seller))
                {
                    await _roleManager.CreateAsync(new IdentityRole<int> { Name = Roles.Seller, NormalizedName = Roles.Seller.ToUpper() });
                }

                // Add Seller role if user doesn't already have it
                var user = await _userManager.FindByIdAsync(seller.UserId.ToString());
                if (user != null && !await _userManager.IsInRoleAsync(user, Roles.Seller))
                {
                    await _userManager.AddToRoleAsync(user, Roles.Seller);
                    _logger.LogInformation("Added Seller role to user {UserId} via webhook", seller.UserId);
                }
            }

            _logger.LogInformation(
                "Successfully processed MERCHANT.ONBOARDING.COMPLETED for seller {SellerId}, IsActive={IsActive}",
                seller?.Id, seller?.IsActive);

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling MERCHANT.ONBOARDING.COMPLETED webhook");
            return StatusCode(500, "Error processing webhook");
        }
    }

    /// <summary>
    /// Handles the MERCHANT.PARTNER-CONSENT.REVOKED event.
    /// This is triggered when a seller revokes their consent to the platform.
    /// </summary>
    private async Task<IActionResult> HandleMerchantConsentRevokedAsync(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("resource", out var resource))
            {
                _logger.LogWarning("MERCHANT.PARTNER-CONSENT.REVOKED webhook missing resource");
                return BadRequest("Missing resource");
            }

            // Extract merchant ID from the webhook
            string? merchantId = null;
            string? trackingId = null;

            if (resource.TryGetProperty("merchant_id", out var merchantIdElement))
            {
                merchantId = merchantIdElement.GetString();
            }

            if (resource.TryGetProperty("tracking_id", out var trackingIdElement))
            {
                trackingId = trackingIdElement.GetString();
            }

            _logger.LogInformation(
                "Processing MERCHANT.PARTNER-CONSENT.REVOKED: MerchantId={MerchantId}, TrackingId={TrackingId}",
                merchantId, trackingId);

            // Find the seller by merchant ID or tracking ID
            Seller? seller = null;
            if (!string.IsNullOrWhiteSpace(merchantId))
            {
                seller = await _sellerService.GetSellerByMerchantIdAsync(merchantId);
            }
            if (seller == null && !string.IsNullOrWhiteSpace(trackingId))
            {
                seller = await _sellerService.GetSellerByTrackingIdAsync(trackingId);
            }

            if (seller == null)
            {
                _logger.LogWarning(
                    "Could not find seller for MERCHANT.PARTNER-CONSENT.REVOKED webhook: " +
                    "MerchantId={MerchantId}, TrackingId={TrackingId}",
                    merchantId, trackingId);
                // Return 200 OK to prevent PayPal from retrying
                return Ok(new { status = "seller_not_found" });
            }

            // Revoke the seller's consent - this will deactivate their account and songs
            await _sellerService.RevokeSellerConsentAsync(seller.Id);

            // Remove Seller role from the user
            var user = await _userManager.FindByIdAsync(seller.UserId.ToString());
            if (user != null && await _userManager.IsInRoleAsync(user, Roles.Seller))
            {
                await _userManager.RemoveFromRoleAsync(user, Roles.Seller);
                _logger.LogInformation("Removed Seller role from user {UserId} via consent revocation webhook", seller.UserId);
            }

            _logger.LogInformation(
                "Successfully processed MERCHANT.PARTNER-CONSENT.REVOKED for seller {SellerId}",
                seller.Id);

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling MERCHANT.PARTNER-CONSENT.REVOKED webhook");
            return StatusCode(500, "Error processing webhook");
        }
    }

    /// <summary>
    /// Handles unknown webhook event types.
    /// </summary>
    private IActionResult HandleUnknownEvent(string? eventType)
    {
        _logger.LogInformation("Received unknown PayPal webhook event type: {EventType}", eventType);
        // Return 200 OK to acknowledge receipt - we don't need to process this event
        return Ok(new { status = "ignored", event_type = eventType });
    }

    /// <summary>
    /// Verifies the PayPal webhook signature.
    /// See: https://developer.paypal.com/api/rest/webhooks/rest-webhooks/#verify-webhook-signature
    /// </summary>
    private async Task<bool> VerifyWebhookSignatureAsync(string body, string webhookId)
    {
        try
        {
            // Get the required headers from the request
            var transmissionId = Request.Headers["PayPal-Transmission-Id"].FirstOrDefault();
            var transmissionTime = Request.Headers["PayPal-Transmission-Time"].FirstOrDefault();
            var certUrl = Request.Headers["PayPal-Cert-Url"].FirstOrDefault();
            var transmissionSig = Request.Headers["PayPal-Transmission-Sig"].FirstOrDefault();
            var authAlgo = Request.Headers["PayPal-Auth-Algo"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(transmissionId) ||
                string.IsNullOrWhiteSpace(transmissionTime) ||
                string.IsNullOrWhiteSpace(certUrl) ||
                string.IsNullOrWhiteSpace(transmissionSig) ||
                string.IsNullOrWhiteSpace(authAlgo))
            {
                _logger.LogWarning("Missing PayPal webhook signature headers");
                return false;
            }

            // Compute CRC32 of the body
            var crc32 = ComputeCrc32(body);

            // Build the expected signature string
            // Format: <transmissionId>|<transmissionTime>|<webhookId>|<crc32>
            var expectedSignatureString = $"{transmissionId}|{transmissionTime}|{webhookId}|{crc32}";

            _logger.LogDebug(
                "Webhook signature verification: TransmissionId={TransmissionId}, Time={Time}, " +
                "WebhookId={WebhookId}, CRC32={CRC32}",
                transmissionId, transmissionTime, webhookId, crc32);

            // For production, you would verify the signature using the certificate from certUrl
            // and the PayPal API. For now, we'll log the verification attempt.
            // In a real implementation, you would call PayPal's verify-webhook-signature API.

            // Call PayPal's verify-webhook-signature API
            return await VerifyWithPayPalApiAsync(
                transmissionId, transmissionTime, certUrl, authAlgo,
                transmissionSig, webhookId, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying PayPal webhook signature");
            return false;
        }
    }

    /// <summary>
    /// Verifies the webhook signature using PayPal's API.
    /// </summary>
    private async Task<bool> VerifyWithPayPalApiAsync(
        string transmissionId, string transmissionTime, string certUrl,
        string authAlgo, string transmissionSig, string webhookId, string body)
    {
        try
        {
            var clientId = _configuration["PayPal:ClientId"];
            var secret = _configuration["PayPal:Secret"];
            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret) ||
                clientId.Contains("REPLACE", StringComparison.OrdinalIgnoreCase) ||
                secret.Contains("REPLACE", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("PayPal credentials not configured - skipping webhook verification");
                return true; // Allow in development when credentials aren't configured
            }

            // Get access token using IHttpClientFactory
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(baseUrl);
            
            var authBytes = Encoding.ASCII.GetBytes($"{clientId}:{secret}");
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            var tokenContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });
            var tokenResponse = await httpClient.PostAsync("v1/oauth2/token", tokenContent);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get PayPal access token for webhook verification");
                return false;
            }

            var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
            using var tokenDoc = JsonDocument.Parse(tokenBody);
            var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();

            // Create a new client for verification to avoid header conflicts
            var verifyClient = _httpClientFactory.CreateClient();
            verifyClient.BaseAddress = new Uri(baseUrl);
            verifyClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            var verifyRequest = new
            {
                transmission_id = transmissionId,
                transmission_time = transmissionTime,
                cert_url = certUrl,
                auth_algo = authAlgo,
                transmission_sig = transmissionSig,
                webhook_id = webhookId,
                webhook_event = JsonSerializer.Deserialize<JsonElement>(body)
            };

            var verifyContent = new StringContent(
                JsonSerializer.Serialize(verifyRequest),
                Encoding.UTF8,
                "application/json");

            var verifyResponse = await verifyClient.PostAsync(
                "v1/notifications/verify-webhook-signature",
                verifyContent);

            var verifyBody = await verifyResponse.Content.ReadAsStringAsync();

            if (!verifyResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal webhook verification API call failed: {Status} {Body}",
                    verifyResponse.StatusCode, verifyBody);
                return false;
            }

            using var verifyDoc = JsonDocument.Parse(verifyBody);
            if (verifyDoc.RootElement.TryGetProperty("verification_status", out var statusElement))
            {
                var status = statusElement.GetString();
                var isValid = string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

                if (!isValid)
                {
                    _logger.LogWarning("PayPal webhook verification failed: Status={Status}", status);
                }

                return isValid;
            }

            _logger.LogWarning("PayPal webhook verification response missing verification_status");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling PayPal webhook verification API");
            return false;
        }
    }

    /// <summary>
    /// Computes CRC32 checksum of a string.
    /// </summary>
    private static uint ComputeCrc32(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var crc = new System.IO.Hashing.Crc32();
        crc.Append(bytes);
        var hash = crc.GetCurrentHash();
        // Convert to uint (little-endian)
        return BitConverter.ToUInt32(hash, 0);
    }
}
