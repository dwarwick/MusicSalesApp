using Microsoft.AspNetCore.Mvc;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppleAppStoreNotificationsController> _logger;

    public AppleAppStoreNotificationsController(
        ISubscriptionService subscriptionService,
        IConfiguration configuration,
        ILogger<AppleAppStoreNotificationsController> logger)
    {
        _subscriptionService = subscriptionService;
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

            await _subscriptionService.UpdateAppleSubscriptionStatusAsync(
                transactionPayload.OriginalTransactionId,
                status,
                expiryTime,
                transactionPayload.TransactionId,
                transactionPayload.Environment,
                transactionPayload.ProductId,
                transactionPayload.AppAccountToken,
                3.99m);

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
}

public record AppStoreServerNotificationRequest(string SignedPayload);