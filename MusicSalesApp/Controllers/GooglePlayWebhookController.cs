using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// Receives Real-Time Developer Notifications (RTDN) from Google Play via Cloud Pub/Sub push.
/// This endpoint must be publicly accessible (no auth) because Google sends the notifications.
/// </summary>
[Route("api/google-play")]
[ApiController]
public class GooglePlayWebhookController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IGooglePlayVerificationService _verificationService;
    private readonly IAccountEmailService _accountEmailService;
    private readonly ISubscriptionConfirmationEmailService _subscriptionConfirmationEmailService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GooglePlayWebhookController> _logger;

    public GooglePlayWebhookController(
        ISubscriptionService subscriptionService,
        IGooglePlayVerificationService verificationService,
        IAccountEmailService accountEmailService,
        ISubscriptionConfirmationEmailService subscriptionConfirmationEmailService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<GooglePlayWebhookController> logger)
    {
        _subscriptionService = subscriptionService;
        _verificationService = verificationService;
        _accountEmailService = accountEmailService;
        _subscriptionConfirmationEmailService = subscriptionConfirmationEmailService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Google Cloud Pub/Sub push endpoint for Real-Time Developer Notifications.
    /// </summary>
    [HttpPost("webhook")]
    public async Task<IActionResult> HandleNotification([FromBody] JsonElement body)
    {
        try
        {
            // Cloud Pub/Sub push messages have a "message.data" field (base64-encoded)
            if (!body.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("data", out var dataElement))
            {
                _logger.LogWarning("RTDN webhook received invalid payload (no message.data)");
                return Ok(); // Return 200 so Pub/Sub doesn't retry
            }

            var dataBase64 = dataElement.GetString();
            if (string.IsNullOrEmpty(dataBase64))
            {
                _logger.LogWarning("RTDN webhook received empty data");
                return Ok();
            }

            var dataJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(dataBase64));
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;

            // Check for subscription notification
            if (!root.TryGetProperty("subscriptionNotification", out var subNotification))
            {
                _logger.LogInformation("RTDN webhook received non-subscription notification, ignoring");
                return Ok();
            }

            var purchaseToken = subNotification.TryGetProperty("purchaseToken", out var tokenElem)
                ? tokenElem.GetString() : null;
            var notificationType = subNotification.TryGetProperty("notificationType", out var typeElem)
                ? typeElem.GetInt32() : 0;

            if (string.IsNullOrEmpty(purchaseToken))
            {
                _logger.LogWarning("RTDN subscription notification missing purchaseToken");
                return Ok();
            }

            _logger.LogInformation("RTDN received notification type {Type} for purchase token", notificationType);

            await ProcessSubscriptionNotificationAsync(purchaseToken, notificationType);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing RTDN webhook");
            return Ok(); // Return 200 so Pub/Sub doesn't endlessly retry
        }
    }

    private async Task ProcessSubscriptionNotificationAsync(string purchaseToken, int notificationType)
    {
        // Google RTDN notification types:
        // 1 = RECOVERED, 2 = RENEWED, 3 = CANCELED, 4 = PURCHASED,
        // 5 = ON_HOLD, 6 = IN_GRACE_PERIOD, 7 = RESTARTED,
        // 9 = DEFERRED, 10 = PAUSED, 11 = PAUSE_SCHEDULE_CHANGED,
        // 12 = REVOKED, 13 = EXPIRED, 20 = PENDING_PURCHASE_CANCELED

        var notificationStatus = notificationType switch
        {
            1 or 2 or 4 or 7 => SubscriptionStatuses.Active,      // RECOVERED, RENEWED, PURCHASED, RESTARTED
            3 => SubscriptionStatuses.Cancelled,                    // CANCELED
            5 or 6 or 10 => SubscriptionStatuses.Suspended,        // ON_HOLD, IN_GRACE_PERIOD, PAUSED
            12 or 13 => SubscriptionStatuses.Expired,              // REVOKED, EXPIRED
            _ => (string)null
        };

        if (notificationStatus == null)
        {
            _logger.LogInformation("RTDN notification type {Type} does not require status update", notificationType);
            return;
        }

        var existingSubscription = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(purchaseToken);

        var productId = _configuration["GooglePlay:SubscriptionProductId"] ?? "streamtunes_monthly_sub";
        var info = await _verificationService.VerifySubscriptionAsync(purchaseToken, productId);

        DateTime? expiryTime = info?.ExpiryTime?.UtcDateTime;
        var status = ResolveLocalStatus(notificationStatus, info);

        _logger.LogInformation(
            "RTDN notification type {NotificationType} mapped to {NotificationStatus}; Google verification state={GoogleState}, expiry={ExpiryTime}, isFreeTrial={IsFreeTrial}, autoRenewEnabled={AutoRenewEnabled}, recurringPrice={RecurringPrice}. Chosen local status={LocalStatus}.",
            notificationType,
            notificationStatus,
            info?.SubscriptionState,
            expiryTime,
            info?.IsFreeTrial,
            info?.AutoRenewEnabled,
            info?.RecurringPrice,
            status);

        await _subscriptionService.UpdateGooglePlaySubscriptionStatusAsync(purchaseToken, status, expiryTime, info);

        if (status == SubscriptionStatuses.Cancelled)
        {
            await SendCancellationEmailAsync(purchaseToken);
        }

        if (status == SubscriptionStatuses.Active && info?.IsFreeTrial == false)
        {
            await SendTrialConvertedEmailIfNeededAsync(purchaseToken, existingSubscription);
        }
    }

    private static string ResolveLocalStatus(string notificationStatus, GooglePlaySubscriptionInfo info)
    {
        return info.SubscriptionState switch
        {
            "SUBSCRIPTION_STATE_ACTIVE" => SubscriptionStatuses.Active,
            "SUBSCRIPTION_STATE_CANCELED" => SubscriptionStatuses.Cancelled,
            "SUBSCRIPTION_STATE_EXPIRED" => SubscriptionStatuses.Expired,
            _ => notificationStatus
        };
    }

    private async Task SendTrialConvertedEmailIfNeededAsync(string purchaseToken, Subscription existingSubscription)
    {
        if (existingSubscription?.TrialEndDate == null || existingSubscription.TrialConversionEmailSentAt.HasValue)
        {
            return;
        }

        var subscription = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(purchaseToken);
        if (subscription == null)
        {
            return;
        }

        var user = await _userManager.FindByIdAsync(subscription.UserId.ToString());
        if (user == null || string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        if (await _subscriptionConfirmationEmailService.SendTrialConvertedAsync(user, subscription, GetBaseUrl()))
        {
            await _subscriptionService.MarkTrialConversionEmailSentAsync(subscription.Id);
        }
    }

    private async Task SendCancellationEmailAsync(string purchaseToken)
    {
        var subscription = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(purchaseToken);
        if (subscription == null)
        {
            return;
        }

        var user = await _userManager.FindByIdAsync(subscription.UserId.ToString());
        if (user == null || string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        await _accountEmailService.SendSubscriptionCancelledEmailAsync(
            user.Email,
            user.UserName ?? user.Email,
            subscription.EndDate,
            subscription.BillingSource,
            user.TimeZoneId,
            GetBaseUrl());
    }

    private string GetBaseUrl()
    {
        var baseUrl = _configuration["BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        return $"{Request.Scheme}://{Request.Host}";
    }
}
