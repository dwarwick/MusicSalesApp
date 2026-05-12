#nullable enable
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// Handles PayPal webhook events including dispute/chargeback notifications.
/// This endpoint must be publicly accessible (AllowAnonymous) because PayPal
/// sends webhooks from its servers without authentication cookies.
/// </summary>
[Route("api/paypal")]
[ApiController]
[AllowAnonymous]
public class PayPalWebhookController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAdminNotificationService _adminNotificationService;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<PayPalWebhookController> _logger;

    public PayPalWebhookController(
        IDbContextFactory<AppDbContext> contextFactory,
        ISubscriptionService subscriptionService,
        IAdminNotificationService adminNotificationService,
        IEmailService emailService,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        UserManager<ApplicationUser> userManager,
        IWebHostEnvironment environment,
        ILogger<PayPalWebhookController> logger)
    {
        _contextFactory = contextFactory;
        _subscriptionService = subscriptionService;
        _adminNotificationService = adminNotificationService;
        _emailService = emailService;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _userManager = userManager;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Receives PayPal webhook events for subscription lifecycle changes and disputes.
    /// </summary>
    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook()
    {
        string body;
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            body = await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read PayPal webhook request body");
            return BadRequest("Invalid request body");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequest("Empty request body");
        }

        // Verify webhook signature in production
        var webhookId = _configuration["PayPal:WebhookId"];
        var isPlaceholder = string.IsNullOrEmpty(webhookId) || webhookId.Contains("REPLACE");

        if (!isPlaceholder)
        {
            var verified = await VerifyWebhookSignatureAsync(body, webhookId!);
            if (!verified)
            {
                _logger.LogWarning("PayPal webhook signature verification failed");
                return Unauthorized("Webhook signature verification failed");
            }
        }
        else if (_environment.IsDevelopment())
        {
            _logger.LogInformation("Skipping PayPal webhook signature verification (Development environment with placeholder webhook ID)");
        }
        else
        {
            _logger.LogError("PayPal:WebhookId is missing or placeholder in non-Development environment. Rejecting webhook to prevent forged requests.");
            return Unauthorized("Webhook signature verification is required in production");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse PayPal webhook JSON");
            return BadRequest("Invalid JSON body");
        }

        using (doc)
        {
            var eventType = doc.RootElement.TryGetProperty("event_type", out var eventTypeProp)
                ? eventTypeProp.GetString()
                : null;

            if (IsSubscriptionLifecycleEvent(eventType))
            {
                await HandleSubscriptionLifecycleEventAsync(eventType!, doc.RootElement);
            }
            else if (eventType == "CUSTOMER.DISPUTE.CREATED")
            {
                await HandleDisputeCreatedAsync(doc.RootElement);
            }
            else
            {
                _logger.LogInformation("Ignoring PayPal webhook event type: {EventType}", eventType);
            }
        }

        return Ok();
    }

    private async Task HandleSubscriptionLifecycleEventAsync(string eventType, JsonElement root)
    {
        if (!root.TryGetProperty("resource", out var resource))
        {
            _logger.LogWarning("PayPal subscription webhook {EventType} missing resource", eventType);
            return;
        }

        var paypalSubscriptionId = resource.TryGetProperty("id", out var idProperty)
            ? idProperty.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(paypalSubscriptionId))
        {
            _logger.LogWarning("PayPal subscription webhook {EventType} missing subscription ID", eventType);
            return;
        }

        var status = ResolveSubscriptionStatus(eventType, resource);
        if (string.IsNullOrWhiteSpace(status))
        {
            _logger.LogInformation("Ignoring PayPal subscription webhook {EventType} because it did not resolve to a supported status", eventType);
            return;
        }

        var nextBillingDate = TryGetNextBillingDate(resource);
        await _subscriptionService.UpdateSubscriptionStatusAsync(paypalSubscriptionId, status, nextBillingDate);

        _logger.LogInformation(
            "Processed PayPal subscription webhook {EventType} for subscription {SubscriptionId} with status {Status}",
            eventType,
            paypalSubscriptionId,
            status);
    }

    private static bool IsSubscriptionLifecycleEvent(string? eventType)
        => eventType is "BILLING.SUBSCRIPTION.ACTIVATED"
            or "BILLING.SUBSCRIPTION.CANCELLED"
            or "BILLING.SUBSCRIPTION.EXPIRED"
            or "BILLING.SUBSCRIPTION.SUSPENDED"
            or "BILLING.SUBSCRIPTION.UPDATED";

    private static string? ResolveSubscriptionStatus(string eventType, JsonElement resource)
    {
        return eventType switch
        {
            "BILLING.SUBSCRIPTION.ACTIVATED" => SubscriptionStatuses.Active,
            "BILLING.SUBSCRIPTION.CANCELLED" => SubscriptionStatuses.Cancelled,
            "BILLING.SUBSCRIPTION.EXPIRED" => SubscriptionStatuses.Expired,
            "BILLING.SUBSCRIPTION.SUSPENDED" => SubscriptionStatuses.Suspended,
            "BILLING.SUBSCRIPTION.UPDATED" => MapPayPalSubscriptionStatus(
                resource.TryGetProperty("status", out var statusProperty)
                    ? statusProperty.GetString()
                    : null),
            _ => null
        };
    }

    private static string? MapPayPalSubscriptionStatus(string? paypalStatus)
    {
        return paypalStatus?.ToUpperInvariant() switch
        {
            SubscriptionStatuses.ApprovalPending => SubscriptionStatuses.ApprovalPending,
            SubscriptionStatuses.Active => SubscriptionStatuses.Active,
            SubscriptionStatuses.Cancelled => SubscriptionStatuses.Cancelled,
            SubscriptionStatuses.Suspended => SubscriptionStatuses.Suspended,
            SubscriptionStatuses.Expired => SubscriptionStatuses.Expired,
            _ => null
        };
    }

    private static DateTime? TryGetNextBillingDate(JsonElement resource)
    {
        if (!resource.TryGetProperty("billing_info", out var billingInfo) ||
            !billingInfo.TryGetProperty("next_billing_time", out var nextBillingProperty))
        {
            return null;
        }

        var nextBillingText = nextBillingProperty.GetString();
        return DateTime.TryParse(
            nextBillingText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private async Task HandleDisputeCreatedAsync(JsonElement root)
    {
        var receivedAt = DateTime.UtcNow;

        // Extract dispute details from the resource
        if (!root.TryGetProperty("resource", out var resource))
        {
            _logger.LogWarning("PayPal dispute webhook missing 'resource' property");
            return;
        }

        var disputeId = resource.TryGetProperty("dispute_id", out var did) ? did.GetString() : null;
        var reason = resource.TryGetProperty("reason", out var r) ? r.GetString() : null;
        var stage = resource.TryGetProperty("dispute_life_cycle_stage", out var s) ? s.GetString() : null;
        var channel = resource.TryGetProperty("dispute_channel", out var ch) ? ch.GetString() : null;

        // Extract amount
        string? amountStr = null;
        if (resource.TryGetProperty("dispute_amount", out var disputeAmount))
        {
            var value = disputeAmount.TryGetProperty("value", out var v) ? v.GetString() : null;
            var currency = disputeAmount.TryGetProperty("currency_code", out var cc) ? cc.GetString() : null;
            if (value != null)
                amountStr = currency != null ? $"{value} {currency}" : value;
        }

        // Extract seller_transaction_id from disputed_transactions[0]
        string? sellerTransactionId = null;
        if (resource.TryGetProperty("disputed_transactions", out var transactions) &&
            transactions.ValueKind == JsonValueKind.Array &&
            transactions.GetArrayLength() > 0)
        {
            var firstTx = transactions[0];
            sellerTransactionId = firstTx.TryGetProperty("seller_transaction_id", out var stid)
                ? stid.GetString()
                : null;
        }

        _logger.LogInformation(
            "Processing PayPal dispute: DisputeId={DisputeId}, Reason={Reason}, Stage={Stage}, " +
            "Channel={Channel}, Amount={Amount}, SellerTransactionId={SellerTransactionId}",
            disputeId, reason, stage, channel, amountStr, sellerTransactionId);

        if (string.IsNullOrEmpty(sellerTransactionId))
        {
            _logger.LogWarning("PayPal dispute webhook has no seller_transaction_id");
            await CreateChargebackLogAsync(new ChargebackLog
            {
                PayPalDisputeId = disputeId ?? "UNKNOWN",
                SellerTransactionId = sellerTransactionId,
                Reason = reason,
                Stage = stage,
                Channel = channel,
                Amount = amountStr,
                Status = "NO_TRANSACTION_FOUND",
                Notes = "No seller_transaction_id in dispute webhook",
                ReceivedAt = receivedAt
            });
            await SendAdminChargebackNotificationAsync(disputeId, reason, stage, channel, amountStr,
                sellerTransactionId, null, "NO_TRANSACTION_FOUND",
                "No seller_transaction_id found in dispute payload. Manual review required.");
            return;
        }

        // Step 1: Check if this is a subscription payment
        var subscriptionHandled = await TryHandleSubscriptionChargebackAsync(
            disputeId, sellerTransactionId, reason, stage, channel, amountStr, receivedAt);

        if (subscriptionHandled)
            return;

        // Step 2: Check if this is a tip payment
        var tipHandled = await TryHandleTipChargebackAsync(
            disputeId, sellerTransactionId, reason, stage, channel, amountStr, receivedAt);

        if (tipHandled)
            return;

        // Step 3: No matching transaction found
        _logger.LogWarning("No matching subscription or tip found for seller_transaction_id {TransactionId}",
            sellerTransactionId);

        await CreateChargebackLogAsync(new ChargebackLog
        {
            PayPalDisputeId = disputeId ?? "UNKNOWN",
            SellerTransactionId = sellerTransactionId,
            Reason = reason,
            Stage = stage,
            Channel = channel,
            Amount = amountStr,
            Status = "NO_TRANSACTION_FOUND",
            Notes = "No matching subscription or tip found for this transaction",
            ReceivedAt = receivedAt
        });

        await SendAdminChargebackNotificationAsync(disputeId, reason, stage, channel, amountStr,
            sellerTransactionId, null, "NO_TRANSACTION_FOUND",
            "No matching subscription or tip found. Manual review required.");
    }

    /// <summary>
    /// Attempts to identify the disputed transaction as a subscription payment.
    /// Calls PayPal's GET /v1/payments/sale/{transactionId} to check for billing_agreement_id.
    /// </summary>
    private async Task<bool> TryHandleSubscriptionChargebackAsync(
        string? disputeId, string sellerTransactionId, string? reason,
        string? stage, string? channel, string? amountStr, DateTime receivedAt)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Failed to get PayPal access token for sale lookup");
                return false;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync($"v1/payments/sale/{sellerTransactionId}");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "PayPal sale lookup failed for {TransactionId}: {Status}. Falling through to tip check.",
                    sellerTransactionId, response.StatusCode);
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("billing_agreement_id", out var billingAgreementProp))
            {
                _logger.LogInformation(
                    "No billing_agreement_id for transaction {TransactionId}. Falling through to tip check.",
                    sellerTransactionId);
                return false;
            }

            var paypalSubscriptionId = billingAgreementProp.GetString();
            if (string.IsNullOrEmpty(paypalSubscriptionId))
                return false;

            // Look up subscription in our database
            var subscription = await _subscriptionService.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
            if (subscription == null)
            {
                _logger.LogWarning(
                    "PayPal subscription {SubscriptionId} not found in database for dispute {DisputeId}",
                    paypalSubscriptionId, disputeId);
                return false;
            }

            // Found a subscription — process the chargeback
            await ProcessSubscriptionChargebackAsync(
                subscription, disputeId, sellerTransactionId, paypalSubscriptionId,
                reason, stage, channel, amountStr, receivedAt);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during subscription chargeback lookup for transaction {TransactionId}",
                sellerTransactionId);
            return false;
        }
    }

    private async Task ProcessSubscriptionChargebackAsync(
        Subscription subscription, string? disputeId, string sellerTransactionId,
        string paypalSubscriptionId, string? reason, string? stage,
        string? channel, string? amountStr, DateTime receivedAt)
    {
        var notes = new StringBuilder();

        // 1. Cancel subscription in our database
        try
        {
            await _subscriptionService.UpdateSubscriptionStatusAsync(paypalSubscriptionId, SubscriptionStatuses.Cancelled);
            notes.AppendLine("Subscription cancelled in database.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel subscription {SubscriptionId} in database", paypalSubscriptionId);
            notes.AppendLine($"Failed to cancel subscription in database: {ex.Message}");
        }

        // 2. Cancel subscription on PayPal
        try
        {
            await CancelPayPalSubscriptionAsync(paypalSubscriptionId);
            notes.AppendLine("Subscription cancelled on PayPal.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel PayPal subscription {SubscriptionId}", paypalSubscriptionId);
            notes.AppendLine($"Failed to cancel PayPal subscription: {ex.Message}");
        }

        // 3. Issue refund
        try
        {
            var refundResult = await RefundCaptureAsync(sellerTransactionId);
            notes.AppendLine(refundResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refund capture {CaptureId}", sellerTransactionId);
            notes.AppendLine($"Refund failed: {ex.Message}");
        }

        // Look up user details
        string? userEmail = null;
        int? userId = null;
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == subscription.UserId);
            if (user != null)
            {
                userId = user.Id;
                userEmail = user.Email;

                // Block user from creating new subscriptions after chargeback
                user.IsSubscriptionBlocked = true;
                user.SubscriptionBlockedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to look up user for subscription {SubscriptionId}", paypalSubscriptionId);
        }

        // 4. Create chargeback log
        await CreateChargebackLogAsync(new ChargebackLog
        {
            PayPalDisputeId = disputeId ?? "UNKNOWN",
            UserId = userId,
            UserEmail = userEmail,
            PayPalSubscriptionId = paypalSubscriptionId,
            SellerTransactionId = sellerTransactionId,
            Reason = reason,
            Stage = stage,
            Channel = channel,
            Amount = amountStr,
            Status = "PROCESSED_SUBSCRIPTION",
            Notes = notes.ToString(),
            ReceivedAt = receivedAt
        });

        // 5. Send admin notification
        await SendAdminChargebackNotificationAsync(disputeId, reason, stage, channel, amountStr,
            sellerTransactionId, userEmail, "PROCESSED_SUBSCRIPTION",
            $"Subscription cancelled and refund issued to listener. PayPal Subscription: {paypalSubscriptionId}");

        // 6. Record user history
        if (userId.HasValue && !string.IsNullOrEmpty(userEmail))
        {
            try
            {
                await _adminNotificationService.RecordUserHistoryAsync(
                    userId.Value, userEmail, UserHistoryEventTypes.ChargebackReceived,
                    $"Chargeback received for subscription payment. Dispute ID: {disputeId}, " +
                    $"Reason: {reason}. Subscription {paypalSubscriptionId} cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record user history for chargeback");
            }
        }

        // 7. Send subscriber email
        if (!string.IsNullOrEmpty(userEmail))
        {
            try
            {
                await SendSubscriberChargebackEmailAsync(userEmail, disputeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send chargeback email to subscriber {Email}", userEmail);
            }
        }
    }

    /// <summary>
    /// Attempts to match the disputed transaction to a tip payment via PayPalCaptureId.
    /// </summary>
    private async Task<bool> TryHandleTipChargebackAsync(
        string? disputeId, string sellerTransactionId, string? reason,
        string? stage, string? channel, string? amountStr, DateTime receivedAt)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var tip = await context.Tips
                .Include(t => t.TipperUser)
                .Include(t => t.Creator)
                    .ThenInclude(c => c.User)
                .Include(t => t.SongMetadata)
                .FirstOrDefaultAsync(t => t.PayPalCaptureId == sellerTransactionId);

            if (tip == null)
            {
                _logger.LogInformation("No tip found with PayPalCaptureId {TransactionId}", sellerTransactionId);
                return false;
            }

            var notes = new StringBuilder();

            // 1. Update tip status
            var previousStatus = tip.Status;
            tip.Status = TipStatus.Chargeback;
            notes.AppendLine($"Tip status changed from {previousStatus} to Chargeback.");

            // 2. Warn if tip was already paid out
            if (previousStatus == TipStatus.Paid)
            {
                var warningMsg = "WARNING: This tip was already paid out to the creator. " +
                                 "A manual creator payout reversal may be needed.";
                _logger.LogWarning("Tip {TipId} was already paid out. Payout reversal may be needed.", tip.Id);
                notes.AppendLine(warningMsg);
            }

            // 2b. Block tipper from sending future tips
            if (tip.TipperUser != null)
            {
                tip.TipperUser.IsTipBlocked = true;
                tip.TipperUser.TipBlockedAt = DateTime.UtcNow;
                notes.AppendLine("Tipper has been blocked from sending future tips.");
            }

            await context.SaveChangesAsync();

            // 3. Issue refund
            try
            {
                var refundResult = await RefundCaptureAsync(sellerTransactionId);
                notes.AppendLine(refundResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refund tip capture {CaptureId}", sellerTransactionId);
                notes.AppendLine($"Refund failed: {ex.Message}");
            }

            // Get user info
            var userEmail = tip.TipperUser?.Email;
            int? userId = tip.TipperUserId;

            // 4. Create chargeback log
            await CreateChargebackLogAsync(new ChargebackLog
            {
                PayPalDisputeId = disputeId ?? "UNKNOWN",
                UserId = userId,
                UserEmail = userEmail,
                TipId = tip.Id,
                SellerTransactionId = sellerTransactionId,
                Reason = reason,
                Stage = stage,
                Channel = channel,
                Amount = amountStr,
                Status = "PROCESSED_TIP",
                Notes = notes.ToString(),
                ReceivedAt = receivedAt
            });

            // 5. Send admin notification
            var adminAction = $"Tip #{tip.Id} marked as Chargeback and refund issued.";
            if (previousStatus == TipStatus.Paid)
            {
                adminAction += " WARNING: Tip was already paid out — creator payout reversal may be needed.";
            }

            await SendAdminChargebackNotificationAsync(disputeId, reason, stage, channel, amountStr,
                sellerTransactionId, userEmail, "PROCESSED_TIP", adminAction);

            // 6. Send tipper chargeback email (for all tip chargebacks)
            if (!string.IsNullOrEmpty(userEmail))
            {
                try
                {
                    await SendTipperChargebackEmailAsync(userEmail, disputeId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send tip chargeback email to tipper {Email}", userEmail);
                }
            }

            // 7. If tip was already paid out, send email to creator/artist and admin about manual payout reversal
            if (previousStatus == TipStatus.Paid)
            {
                try
                {
                    var creatorEmail = tip.Creator?.User?.Email;
                    var creatorName = tip.Creator?.DisplayName ?? "Creator";
                    var songTitle = tip.SongMetadata?.SongTitle ?? "Unknown Song";

                    await SendChargebackPayoutReversalEmailAsync(
                        creatorEmail, creatorName, songTitle, tip.Amount,
                        disputeId, sellerTransactionId, reason);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send payout reversal email for tip {TipId}", tip.Id);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during tip chargeback handling for transaction {TransactionId}",
                sellerTransactionId);
            return false;
        }
    }

    // ---- Helper methods ----

    private async Task<bool> VerifyWebhookSignatureAsync(string body, string webhookId)
    {
        try
        {
            // Extract headers
            var transmissionId = Request.Headers["PAYPAL-TRANSMISSION-ID"].FirstOrDefault();
            var transmissionTime = Request.Headers["PAYPAL-TRANSMISSION-TIME"].FirstOrDefault();
            var authAlgo = Request.Headers["PAYPAL-AUTH-ALGO"].FirstOrDefault();
            var certUrl = Request.Headers["PAYPAL-CERT-URL"].FirstOrDefault();
            var transmissionSig = Request.Headers["PAYPAL-TRANSMISSION-SIG"].FirstOrDefault();

            if (string.IsNullOrEmpty(transmissionId) || string.IsNullOrEmpty(transmissionTime) ||
                string.IsNullOrEmpty(authAlgo) || string.IsNullOrEmpty(certUrl) ||
                string.IsNullOrEmpty(transmissionSig))
            {
                _logger.LogWarning("Missing PayPal webhook verification headers");
                return false;
            }

            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
                return false;

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var verifyPayload = new
            {
                auth_algo = authAlgo,
                cert_url = certUrl,
                transmission_id = transmissionId,
                transmission_sig = transmissionSig,
                transmission_time = transmissionTime,
                webhook_id = webhookId,
                webhook_event = JsonSerializer.Deserialize<JsonElement>(body)
            };

            var content = new StringContent(
                JsonSerializer.Serialize(verifyPayload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync("v1/notifications/verify-webhook-signature", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal webhook verification API returned {Status}: {Body}",
                    response.StatusCode, responseBody);
                return false;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var verificationStatus = doc.RootElement.TryGetProperty("verification_status", out var vs)
                ? vs.GetString()
                : null;

            return verificationStatus == "SUCCESS";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying PayPal webhook signature");
            return false;
        }
    }

    private async Task<string> GetPayPalAccessTokenAsync()
    {
        var clientId = _configuration["PayPal:ClientId"];
        var secret = _configuration["PayPal:Secret"];
        var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret) ||
            clientId.Contains("REPLACE") || secret.Contains("REPLACE"))
        {
            _logger.LogError("PayPal ClientId/Secret not configured");
            return string.Empty;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);

            var authBytes = Encoding.ASCII.GetBytes($"{clientId}:{secret}");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            var content = new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });
            var response = await client.PostAsync("v1/oauth2/token", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal token request failed: {Status} {Body}", response.StatusCode, body);
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting PayPal access token");
            return string.Empty;
        }
    }

    private async Task CancelPayPalSubscriptionAsync(string subscriptionId)
    {
        var token = await GetPayPalAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Cannot cancel PayPal subscription {SubscriptionId}: failed to get access token",
                subscriptionId);
            return;
        }

        var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var cancelData = new { reason = "Chargeback received — subscription cancelled" };
        var content = new StringContent(JsonSerializer.Serialize(cancelData), Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"v1/billing/subscriptions/{subscriptionId}/cancel", content);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();

            // 404 RESOURCE_NOT_FOUND means already cancelled
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "PayPal subscription {SubscriptionId} not found — treating as already cancelled",
                    subscriptionId);
                return;
            }

            _logger.LogWarning("Failed to cancel PayPal subscription {SubscriptionId}: {Status} {Body}",
                subscriptionId, response.StatusCode, body);
        }
        else
        {
            _logger.LogInformation("PayPal subscription {SubscriptionId} cancelled due to chargeback",
                subscriptionId);
        }
    }

    /// <summary>
    /// Issues a full refund via POST /v2/payments/captures/{captureId}/refund.
    /// Returns a status message. Treats 422 CAPTURE_FULLY_REFUNDED as idempotent success.
    /// </summary>
    private async Task<string> RefundCaptureAsync(string captureId)
    {
        var token = await GetPayPalAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            return "Refund skipped: failed to get PayPal access token.";

        var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync(
            $"v2/payments/captures/{captureId}/refund",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Refund issued for capture {CaptureId}", captureId);
            return "Refund issued successfully.";
        }

        // 422 with CAPTURE_FULLY_REFUNDED = already refunded (idempotent)
        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var details = doc.RootElement.TryGetProperty("details", out var d) ? d : default;
                if (details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var detail in details.EnumerateArray())
                    {
                        var issue = detail.TryGetProperty("issue", out var issueProp)
                            ? issueProp.GetString() : null;
                        if (issue == "CAPTURE_FULLY_REFUNDED")
                        {
                            _logger.LogInformation("Capture {CaptureId} already fully refunded", captureId);
                            return "Capture already fully refunded (idempotent).";
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to generic error
            }
        }

        _logger.LogWarning("Refund failed for capture {CaptureId}: {Status} {Body}",
            captureId, response.StatusCode, body);
        return $"Refund failed: {response.StatusCode}. {body}";
    }

    private async Task CreateChargebackLogAsync(ChargebackLog log)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            context.ChargebackLogs.Add(log);
            await context.SaveChangesAsync();
            _logger.LogInformation("ChargebackLog created: DisputeId={DisputeId}, Status={Status}",
                log.PayPalDisputeId, log.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create ChargebackLog for dispute {DisputeId}", log.PayPalDisputeId);
        }
    }

    private async Task SendAdminChargebackNotificationAsync(
        string? disputeId, string? reason, string? stage, string? channel,
        string? amount, string? sellerTransactionId, string? userEmail,
        string status, string action)
    {
        try
        {
            if (!await _adminNotificationService.IsNotificationEnabledAsync(
                    AdminNotificationService.NotifyChargebackReceivedKey))
            {
                _logger.LogInformation("Chargeback admin notification disabled by setting");
                return;
            }

            var logoUrl = _emailService.GetLogoUrl();
            var subject = "StreamTunes Admin - Chargeback Received";

            var body = $@"
            <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
                <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                    <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>Chargeback Received</h1>
                </div>
                <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                    <p style='font-size: 16px; color: #333;'>A PayPal dispute/chargeback has been received and processed.</p>
                    <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Dispute ID:</strong> {System.Web.HttpUtility.HtmlEncode(disputeId ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Reason:</strong> {System.Web.HttpUtility.HtmlEncode(reason ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Stage:</strong> {System.Web.HttpUtility.HtmlEncode(stage ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Channel:</strong> {System.Web.HttpUtility.HtmlEncode(channel ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Amount:</strong> {System.Web.HttpUtility.HtmlEncode(amount ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Transaction ID:</strong> {System.Web.HttpUtility.HtmlEncode(sellerTransactionId ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>User:</strong> {System.Web.HttpUtility.HtmlEncode(userEmail ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Status:</strong> {System.Web.HttpUtility.HtmlEncode(status)}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Action Taken:</strong> {System.Web.HttpUtility.HtmlEncode(action)}</p>
                    </div>
                    <p style='font-size: 12px; color: #999;'>Received at: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC")}</p>
                </div>
            </div>";

            await _emailService.SendEmailAsync(AdminNotificationService.AdminEmail, subject, body);
        }
        catch (Exception ex)
        {
            // Notification failure must not block chargeback processing
            _logger.LogError(ex, "Failed to send chargeback admin notification for dispute {DisputeId}", disputeId);
        }
    }

    private async Task SendSubscriberChargebackEmailAsync(string userEmail, string? disputeId)
    {
        try
        {
            var logoUrl = _emailService.GetLogoUrl();
            var subject = "StreamTunes - Subscription Cancelled Due to Chargeback";

            var body = $@"
            <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
                <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                    <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>Subscription Cancelled</h1>
                </div>
                <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                    <p style='font-size: 16px; color: #333;'>Your StreamTunes subscription has been cancelled because a chargeback was filed on your payment.</p>
                    <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Dispute Reference:</strong> {System.Web.HttpUtility.HtmlEncode(disputeId ?? "N/A")}</p>
                    </div>
                    <p style='font-size: 16px; color: #333;'>If you believe this chargeback was filed in error, please contact our support team for assistance.</p>
                    <p style='font-size: 14px; color: #999;'>StreamTunes Support: {_configuration["EmailSettings:CustomerServiceEmail"]}</p>
                </div>
            </div>";

            await _emailService.SendEmailAsync(userEmail, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send chargeback notification email to {Email}", userEmail);
        }
    }

    private async Task SendTipperChargebackEmailAsync(string userEmail, string? disputeId)
    {
        try
        {
            var logoUrl = _emailService.GetLogoUrl();
            var subject = "StreamTunes - Tip Chargeback: Tipping Privileges Revoked";

            var body = $@"
            <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
                <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                    <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>Tipping Privileges Revoked</h1>
                </div>
                <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                    <p style='font-size: 16px; color: #333;'>A chargeback has been filed on a tip payment from your account.</p>
                    <div style='background-color: #fff3f3; padding: 15px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #dc3545;'>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Dispute Reference:</strong> {System.Web.HttpUtility.HtmlEncode(disputeId ?? "N/A")}</p>
                    </div>
                    <p style='font-size: 16px; color: #333;'>As a result, your ability to send tips on StreamTunes has been <strong>permanently revoked</strong>. You will no longer be permitted to tip any creator on the platform.</p>
                    <p style='font-size: 16px; color: #333;'>If you believe this chargeback was filed in error, please contact our support team for assistance.</p>
                    <p style='font-size: 14px; color: #999;'>StreamTunes Support: {_configuration["EmailSettings:CustomerServiceEmail"]}</p>
                </div>
            </div>";

            await _emailService.SendEmailAsync(userEmail, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send tip chargeback notification email to {Email}", userEmail);
        }
    }

    /// <summary>
    /// Sends email to both the creator/artist and admin when a tip chargeback requires
    /// a manual payout reversal because the tip was already paid out.
    /// </summary>
    private async Task SendChargebackPayoutReversalEmailAsync(
        string? creatorEmail, string creatorName, string songTitle,
        decimal tipAmount, string? disputeId, string? sellerTransactionId, string? reason)
    {
        try
        {
            var logoUrl = _emailService.GetLogoUrl();
            var subject = "StreamTunes - Chargeback Requires Manual Payout Reversal";
            var encodedCreatorName = System.Web.HttpUtility.HtmlEncode(creatorName);
            var encodedSongTitle = System.Web.HttpUtility.HtmlEncode(songTitle);

            var body = $@"
            <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
                <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                    <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>Payout Reversal Required</h1>
                </div>
                <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                    <p style='font-size: 16px; color: #333;'>A chargeback has been received for a tip that was already paid out. A manual payout reversal is required.</p>
                    <div style='background-color: #fff3f3; padding: 15px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #dc3545;'>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Artist/Creator:</strong> {encodedCreatorName}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Song:</strong> {encodedSongTitle}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Tip Amount:</strong> <span style='font-size: 18px; font-weight: bold; color: #dc3545;'>${tipAmount:F2}</span></p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Dispute ID:</strong> {System.Web.HttpUtility.HtmlEncode(disputeId ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Transaction ID:</strong> {System.Web.HttpUtility.HtmlEncode(sellerTransactionId ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Reason:</strong> {System.Web.HttpUtility.HtmlEncode(reason ?? "N/A")}</p>
                        <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Date/Time (UTC):</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
                    </div>
                    <p style='font-size: 16px; color: #333;'>The tip payment has been refunded to the tipper. Because the tip was already paid out to the creator, a manual reversal of the creator payout is needed.</p>
                    <p style='font-size: 14px; color: #999;'>This is an automated notification from StreamTunes.</p>
                </div>
            </div>";

            // Send to admin
            await _emailService.SendEmailAsync(AdminNotificationService.AdminEmail, subject, body);

            // Send to creator/artist
            if (!string.IsNullOrEmpty(creatorEmail))
            {
                await _emailService.SendEmailAsync(creatorEmail, subject, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send payout reversal notification emails for dispute {DisputeId}", disputeId);
        }
    }
}
