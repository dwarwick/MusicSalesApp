using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// Receives App Store Server Notifications v2 from Apple.
/// This endpoint must be publicly accessible because Apple delivers notifications directly.
/// </summary>
[Route("api/subscription/app-store")]
[ApiController]
public class AppleAppStoreNotificationsController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAccountEmailService _accountEmailService;
    private readonly ISubscriptionConfirmationEmailService _subscriptionConfirmationEmailService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppleAppStoreNotificationsController> _logger;

    public AppleAppStoreNotificationsController(
        ISubscriptionService subscriptionService,
        IAccountEmailService accountEmailService,
        ISubscriptionConfirmationEmailService subscriptionConfirmationEmailService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<AppleAppStoreNotificationsController> logger)
    {
        _subscriptionService = subscriptionService;
        _accountEmailService = accountEmailService;
        _subscriptionConfirmationEmailService = subscriptionConfirmationEmailService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("notifications")]
    public async Task<IActionResult> HandleNotification([FromBody] AppStoreServerNotificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SignedPayload))
        {
            _logger.LogWarning("Apple App Store notification missing signedPayload");
            return Ok();
        }

        try
        {
            var notification = AppleAppStoreVerificationService.DecodeServerNotificationPayload(request.SignedPayload);
            var transactionPayload = AppleAppStoreVerificationService.DecodeSignedTransactionInfo(notification.Data?.SignedTransactionInfo);
            var renewalInfo = !string.IsNullOrWhiteSpace(notification.Data?.SignedRenewalInfo)
                ? AppleAppStoreVerificationService.DecodeSignedRenewalInfo(notification.Data.SignedRenewalInfo)
                : null;
            var configuredProductId = _configuration["AppleAppStore:SubscriptionProductId"];

            if (!string.IsNullOrWhiteSpace(configuredProductId) &&
                !string.Equals(transactionPayload.ProductId, configuredProductId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Ignoring Apple notification for product {ProductId}; configured product is {ConfiguredProductId}",
                    transactionPayload.ProductId,
                    configuredProductId);
                return Ok();
            }

            if (string.IsNullOrWhiteSpace(transactionPayload.OriginalTransactionId))
            {
                _logger.LogWarning("Ignoring Apple notification without original transaction ID");
                return Ok();
            }

            var expiryTime = transactionPayload.ExpiresDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(transactionPayload.ExpiresDate.Value).UtcDateTime
                : (DateTime?)null;
            var revocationTime = transactionPayload.RevocationDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(transactionPayload.RevocationDate.Value).UtcDateTime
                : (DateTime?)null;

            var status = AppleAppStoreVerificationService.DetermineNotificationStatus(
                notification.NotificationType,
                notification.Subtype,
                DateTime.UtcNow,
                expiryTime,
                revocationTime,
                renewalInfo?.AutoRenewStatus);

            var existingSubscription = await _subscriptionService.GetSubscriptionByAppleOriginalTransactionIdAsync(transactionPayload.OriginalTransactionId);
            var shouldSendConfirmationEmail = string.Equals(status, SubscriptionStatuses.Active, StringComparison.Ordinal) &&
                                              !SubscriptionEntitlementHelper.IsCurrentlyEntitled(existingSubscription);

            await _subscriptionService.UpdateAppleSubscriptionStatusAsync(
                transactionPayload.OriginalTransactionId,
                status,
                expiryTime,
                transactionPayload.TransactionId,
                transactionPayload.Environment,
                transactionPayload.ProductId,
                transactionPayload.AppAccountToken,
                3.99m);

            if (string.Equals(status, MusicSalesApp.Common.Helpers.SubscriptionStatuses.Cancelled, StringComparison.Ordinal))
            {
                await SendCancellationEmailAsync(transactionPayload.OriginalTransactionId);
            }

            if (shouldSendConfirmationEmail)
            {
                await SendConfirmationEmailAsync(transactionPayload.OriginalTransactionId);
            }

            _logger.LogInformation(
                "Processed Apple notification {NotificationType} for original transaction {OriginalTransactionId} with status {Status}",
                notification.NotificationType,
                transactionPayload.OriginalTransactionId,
                status);
        }
        catch (AppleAppStoreVerificationException ex)
        {
            _logger.LogWarning(ex, "Apple App Store notification payload could not be parsed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Apple App Store notification");
        }

        return Ok();
    }

    private async Task SendCancellationEmailAsync(string originalTransactionId)
    {
        var subscription = await _subscriptionService.GetSubscriptionByAppleOriginalTransactionIdAsync(originalTransactionId);
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

    private async Task SendConfirmationEmailAsync(string originalTransactionId)
    {
        var subscription = await _subscriptionService.GetSubscriptionByAppleOriginalTransactionIdAsync(originalTransactionId);
        if (subscription == null)
        {
            return;
        }

        var user = await _userManager.FindByIdAsync(subscription.UserId.ToString());
        if (user == null || string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        await _subscriptionConfirmationEmailService.SendConfirmationAsync(user, subscription, GetBaseUrl());
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

public record AppStoreServerNotificationRequest(string SignedPayload);