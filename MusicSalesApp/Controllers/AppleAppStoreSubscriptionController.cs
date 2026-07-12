using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

[Route("api/subscription/app-store")]
[ApiController]
public class AppleAppStoreSubscriptionController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAppleAppStoreVerificationService _verificationService;
    private readonly ISubscriptionConfirmationEmailService _subscriptionConfirmationEmailService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppleAppStoreSubscriptionController> _logger;

    public AppleAppStoreSubscriptionController(
        ISubscriptionService subscriptionService,
        IAppleAppStoreVerificationService verificationService,
        ISubscriptionConfirmationEmailService subscriptionConfirmationEmailService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<AppleAppStoreSubscriptionController> logger)
    {
        _subscriptionService = subscriptionService;
        _verificationService = verificationService;
        _subscriptionConfirmationEmailService = subscriptionConfirmationEmailService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("verify")]
    [Authorize(Roles = "Admin,User", AuthenticationSchemes = "Identity.Application,Bearer")]
    public async Task<IActionResult> VerifyAndRecordPurchase([FromBody] AppStorePurchaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TransactionId))
        {
            return BadRequest("TransactionId is required.");
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        await UserTimeZonePersistenceHelper.PersistIfProvidedAsync(_userManager, user, request.TimeZoneId, _logger);

        var productId = _configuration["AppleAppStore:SubscriptionProductId"];
        if (string.IsNullOrWhiteSpace(productId))
        {
            return BadRequest(new { success = false, error = "Apple App Store subscription product is not configured on the server." });
        }

        AppleAppStoreSubscriptionInfo subscriptionInfo;
        try
        {
            subscriptionInfo = await _verificationService.VerifySubscriptionAsync(request.TransactionId, productId);
        }
        catch (AppleAppStoreVerificationException ex)
        {
            _logger.LogWarning(ex, "Apple App Store verification failed for user {UserId}", user.Id);
            return BadRequest(new { success = false, error = ex.Message });
        }

        if (subscriptionInfo == null)
        {
            return BadRequest(new { success = false, error = "Could not verify purchase with Apple App Store." });
        }

        if (subscriptionInfo.Status != SubscriptionStatuses.Active)
        {
            _logger.LogWarning(
                "Apple App Store subscription verification returned status {Status} for user {UserId}",
                subscriptionInfo.Status,
                user.Id);
            return BadRequest(new { success = false, error = $"Subscription is not active (status: {subscriptionInfo.Status})." });
        }

        var appAccountToken = string.IsNullOrWhiteSpace(request.AppAccountToken)
            ? subscriptionInfo.AppAccountToken
            : request.AppAccountToken;

        var existing = await _subscriptionService.GetSubscriptionByAppleOriginalTransactionIdAsync(subscriptionInfo.OriginalTransactionId);
        if (existing != null)
        {
            if (existing.UserId != user.Id)
            {
                _logger.LogWarning(
                    "User {UserId} attempted to verify Apple transaction owned by user {OwnerUserId}",
                    user.Id,
                    existing.UserId);
                return Forbid();
            }

            var existingPrice = subscriptionInfo.Price is > 0
                ? subscriptionInfo.Price.Value
                : existing.MonthlyPrice;
            var shouldSendConfirmationEmail = !SubscriptionEntitlementHelper.IsCurrentlyEntitled(existing);

            _logger.LogInformation(
                "Apple App Store verification matched existing subscription {SubscriptionId} for user {UserId}; updating record. SendConfirmationEmail={SendConfirmationEmail}",
                existing.Id,
                user.Id,
                shouldSendConfirmationEmail);

            try
            {
                await _subscriptionService.UpdateAppleSubscriptionStatusAsync(
                    subscriptionInfo.OriginalTransactionId,
                    subscriptionInfo.Status,
                    subscriptionInfo.ExpiryTime,
                    subscriptionInfo.TransactionId,
                    subscriptionInfo.Environment,
                    subscriptionInfo.ProductId,
                    appAccountToken,
                    existingPrice);
            }
            catch (SubscriptionProviderConflictException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Apple subscription reactivation was rejected for user {UserId} because a current {ExistingBillingSource} subscription exists",
                    user.Id,
                    ex.ExistingBillingSource);
                return Conflict(new { success = false, error = ex.Message });
            }
            if (subscriptionInfo.Price is > 0)
            {
                await _subscriptionService.UpdateAppleStorePriceAsync(
                    subscriptionInfo.OriginalTransactionId,
                    subscriptionInfo.Price.Value,
                    subscriptionInfo.PriceCurrencyCode);
            }

            if (shouldSendConfirmationEmail)
            {
                var updatedSubscription = await _subscriptionService.GetSubscriptionByAppleOriginalTransactionIdAsync(subscriptionInfo.OriginalTransactionId);
                if (updatedSubscription != null)
                {
                    await _subscriptionConfirmationEmailService.SendConfirmationAsync(user, updatedSubscription, GetBaseUrl());
                }
            }

            return Ok(new { success = true, subscriptionId = existing.Id, status = subscriptionInfo.Status });
        }

        if (subscriptionInfo.Price is not > 0)
        {
            _logger.LogWarning(
                "Apple App Store did not return the recurring price for new transaction {TransactionId} owned by user {UserId}",
                subscriptionInfo.TransactionId,
                user.Id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { success = false, error = "Apple App Store could not confirm the subscription price. Please try again later." });
        }

        var monthlyPrice = subscriptionInfo.Price.Value;

        Subscription subscription;
        try
        {
            subscription = await _subscriptionService.CreateAppleSubscriptionAsync(
                user.Id,
                subscriptionInfo.TransactionId,
                subscriptionInfo.OriginalTransactionId,
                subscriptionInfo.ProductId,
                appAccountToken,
                subscriptionInfo.Environment,
                monthlyPrice,
                subscriptionInfo.PurchaseTime);
        }
        catch (SubscriptionProviderConflictException ex)
        {
            _logger.LogWarning(
                ex,
                "Apple subscription creation was rejected for user {UserId} because a current {ExistingBillingSource} subscription exists",
                user.Id,
                ex.ExistingBillingSource);
            return Conflict(new { success = false, error = ex.Message });
        }

        await _subscriptionService.UpdateAppleSubscriptionStatusAsync(
            subscriptionInfo.OriginalTransactionId,
            subscriptionInfo.Status,
            subscriptionInfo.ExpiryTime,
            subscriptionInfo.TransactionId,
            subscriptionInfo.Environment,
            subscriptionInfo.ProductId,
            appAccountToken,
            monthlyPrice);
        await _subscriptionService.UpdateAppleStorePriceAsync(
            subscriptionInfo.OriginalTransactionId,
            monthlyPrice,
            subscriptionInfo.PriceCurrencyCode);

        var refreshedSubscription = await _subscriptionService.GetSubscriptionByAppleOriginalTransactionIdAsync(subscriptionInfo.OriginalTransactionId)
            ?? subscription;

        await _subscriptionConfirmationEmailService.SendConfirmationAsync(user, refreshedSubscription, GetBaseUrl());

        return Ok(new { success = true, subscriptionId = subscription.Id, status = subscriptionInfo.Status });
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

public record AppStorePurchaseRequest(string TransactionId, string AppAccountToken = "", string TimeZoneId = "");
