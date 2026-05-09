using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

[Route("api/subscription/app-store")]
[ApiController]
public class AppleAppStoreSubscriptionController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAppleAppStoreVerificationService _verificationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppleAppStoreSubscriptionController> _logger;

    public AppleAppStoreSubscriptionController(
        ISubscriptionService subscriptionService,
        IAppleAppStoreVerificationService verificationService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<AppleAppStoreSubscriptionController> logger)
    {
        _subscriptionService = subscriptionService;
        _verificationService = verificationService;
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

        var existing = await _subscriptionService.GetSubscriptionByAppleOriginalTransactionIdAsync(subscriptionInfo.OriginalTransactionId);
        if (existing != null)
        {
            await _subscriptionService.UpdateAppleSubscriptionStatusAsync(
                subscriptionInfo.OriginalTransactionId,
                subscriptionInfo.Status,
                subscriptionInfo.ExpiryTime,
                subscriptionInfo.TransactionId,
                subscriptionInfo.Environment);

            return Ok(new { success = true, subscriptionId = existing.Id, status = subscriptionInfo.Status });
        }

        var monthlyPrice = decimal.TryParse(_configuration["AppSettings:SubscriptionPrice"], out var price) ? price : 3.99m;
        var appAccountToken = string.IsNullOrWhiteSpace(request.AppAccountToken)
            ? subscriptionInfo.AppAccountToken
            : request.AppAccountToken;

        var subscription = await _subscriptionService.CreateAppleSubscriptionAsync(
            user.Id,
            subscriptionInfo.TransactionId,
            subscriptionInfo.OriginalTransactionId,
            subscriptionInfo.ProductId,
            appAccountToken,
            subscriptionInfo.Environment,
            monthlyPrice);

        await _subscriptionService.UpdateAppleSubscriptionStatusAsync(
            subscriptionInfo.OriginalTransactionId,
            subscriptionInfo.Status,
            subscriptionInfo.ExpiryTime,
            subscriptionInfo.TransactionId,
            subscriptionInfo.Environment);

        return Ok(new { success = true, subscriptionId = subscription.Id, status = subscriptionInfo.Status });
    }
}

public record AppStorePurchaseRequest(string TransactionId, string AppAccountToken = "");