using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

[Route("api/subscription/google-play")]
[ApiController]
public class GooglePlaySubscriptionController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IGooglePlayVerificationService _verificationService;
    private readonly ISubscriptionConfirmationEmailService _subscriptionConfirmationEmailService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GooglePlaySubscriptionController> _logger;

    public GooglePlaySubscriptionController(
        ISubscriptionService subscriptionService,
        IGooglePlayVerificationService verificationService,
        ISubscriptionConfirmationEmailService subscriptionConfirmationEmailService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<GooglePlaySubscriptionController> logger)
    {
        _subscriptionService = subscriptionService;
        _verificationService = verificationService;
        _subscriptionConfirmationEmailService = subscriptionConfirmationEmailService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Called by the MAUI app after a successful Google Play purchase to verify and record the subscription.
    /// </summary>
    [HttpPost("verify")]
    [Authorize(Roles = "Admin,User", AuthenticationSchemes = "Identity.Application,Bearer")]
    public async Task<IActionResult> VerifyAndRecordPurchase([FromBody] GooglePlayPurchaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PurchaseToken))
            return BadRequest("PurchaseToken is required.");

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return Unauthorized();

        await UserTimeZonePersistenceHelper.PersistIfProvidedAsync(_userManager, user, request.TimeZoneId, _logger);

        var productId = _configuration["GooglePlay:SubscriptionProductId"] ?? "streamtunes_monthly_sub";

        // Verify with Google Play Developer API
        GooglePlaySubscriptionInfo subscriptionInfo;
        try
        {
            subscriptionInfo = await _verificationService.VerifySubscriptionAsync(request.PurchaseToken, productId);
        }
        catch (GooglePlayVerificationException ex)
        {
            _logger.LogWarning(ex, "Google Play verification failed for user {UserId}", user.Id);
            return BadRequest(new { success = false, error = ex.Message });
        }

        if (subscriptionInfo == null)
            return BadRequest(new { success = false, error = "Could not verify purchase with Google Play." });

        // Only allow active subscriptions
        if (subscriptionInfo.SubscriptionState != "SUBSCRIPTION_STATE_ACTIVE")
        {
            _logger.LogWarning("Google Play subscription verification returned state {State} for user {UserId}",
                subscriptionInfo.SubscriptionState, user.Id);
            return BadRequest(new { success = false, error = $"Subscription is not active (state: {subscriptionInfo.SubscriptionState})." });
        }

        var orderId = subscriptionInfo.OrderId ?? request.OrderId ?? "";
        var monthlyPrice = decimal.TryParse(_configuration["AppSettings:SubscriptionPrice"], out var price) ? price : 3.99m;

        // Check if we already have this token recorded
        var existing = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(request.PurchaseToken);
        if (existing != null)
        {
            var shouldSendConfirmationEmail = !string.Equals(existing.Status, SubscriptionStatuses.Active, StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation(
                "Google Play verification matched existing subscription {SubscriptionId} for user {UserId}; refreshing status. SendConfirmationEmail={SendConfirmationEmail}",
                existing.Id,
                user.Id,
                shouldSendConfirmationEmail);

            await _subscriptionService.UpdateGooglePlaySubscriptionStatusAsync(
                request.PurchaseToken,
                SubscriptionStatuses.Active,
                subscriptionInfo.ExpiryTime?.UtcDateTime);

            if (shouldSendConfirmationEmail)
            {
                var updatedSubscription = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(request.PurchaseToken);
                if (updatedSubscription != null)
                {
                    await _subscriptionConfirmationEmailService.SendConfirmationAsync(user, updatedSubscription, GetBaseUrl());
                }
            }

            return Ok(new { success = true, subscriptionId = existing.Id, status = existing.Status });
        }

        // Create new subscription record
        var subscription = await _subscriptionService.CreateGooglePlaySubscriptionAsync(
            user.Id, request.PurchaseToken, orderId, monthlyPrice);

        await _subscriptionService.UpdateGooglePlaySubscriptionStatusAsync(
            request.PurchaseToken,
            SubscriptionStatuses.Active,
            subscriptionInfo.ExpiryTime?.UtcDateTime);

        // Acknowledge the purchase so Google doesn't auto-refund
        if (!subscriptionInfo.IsAcknowledged)
        {
            await _verificationService.AcknowledgeSubscriptionAsync(request.PurchaseToken, productId);
        }

        var refreshedSubscription = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(request.PurchaseToken)
            ?? subscription;

        await _subscriptionConfirmationEmailService.SendConfirmationAsync(user, refreshedSubscription, GetBaseUrl());

        return Ok(new { success = true, subscriptionId = subscription.Id, status = subscription.Status });
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

public record GooglePlayPurchaseRequest(string PurchaseToken, string OrderId = "", string TimeZoneId = "");
