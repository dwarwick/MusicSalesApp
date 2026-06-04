using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Globalization;

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

        if (!HasGooglePlayEntitlement(subscriptionInfo))
        {
            _logger.LogWarning("Google Play subscription verification returned state {State} for user {UserId}",
                subscriptionInfo.SubscriptionState, user.Id);
            return BadRequest(new { success = false, error = $"Subscription is not active (state: {subscriptionInfo.SubscriptionState})." });
        }

        var orderId = subscriptionInfo.OrderId ?? request.OrderId ?? "";
        var priceResolution = ResolveMonthlyPrice(subscriptionInfo, request);
        var monthlyPrice = priceResolution.MonthlyPrice;
        var localStatus = ResolveLocalStatus(subscriptionInfo);

        _logger.LogInformation(
            "Recording Google Play subscription for user {UserId}: GoogleState={GoogleState}, LocalStatus={LocalStatus}, IsFreeTrial={IsFreeTrial}, Expiry={ExpiryTime}, AutoRenewEnabled={AutoRenewEnabled}, OrderId={OrderId}, MonthlyPrice={MonthlyPrice}, PriceSource={PriceSource}, RequestPriceAmountMicros={RequestPriceAmountMicros}, RequestPriceCurrencyCode={RequestPriceCurrencyCode}, RequestFormattedPrice={RequestFormattedPrice}",
            user.Id,
            subscriptionInfo.SubscriptionState,
            localStatus,
            subscriptionInfo.IsFreeTrial,
            subscriptionInfo.ExpiryTime,
            subscriptionInfo.AutoRenewEnabled,
            orderId,
            monthlyPrice,
            priceResolution.PriceSource,
            request.PriceAmountMicros,
            request.PriceCurrencyCode,
            request.FormattedPrice);

        // Check if we already have this token recorded
        var existing = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(request.PurchaseToken);
        if (existing != null)
        {
            var shouldSendConfirmationEmail = !SubscriptionEntitlementHelper.IsCurrentlyEntitled(existing);
            var shouldSendTrialStartedEmail = subscriptionInfo.IsFreeTrial && !existing.TrialActivationEmailSentAt.HasValue;
            var shouldSendTrialConvertedEmail = !subscriptionInfo.IsFreeTrial && existing.TrialEndDate.HasValue && !existing.TrialConversionEmailSentAt.HasValue;

            _logger.LogInformation(
                "Google Play verification matched existing subscription {SubscriptionId} for user {UserId}; refreshing status. SendConfirmationEmail={SendConfirmationEmail}, SendTrialStartedEmail={SendTrialStartedEmail}, SendTrialConvertedEmail={SendTrialConvertedEmail}",
                existing.Id,
                user.Id,
                shouldSendConfirmationEmail,
                shouldSendTrialStartedEmail,
                shouldSendTrialConvertedEmail);

            await _subscriptionService.UpdateGooglePlaySubscriptionStatusAsync(
                request.PurchaseToken,
                localStatus,
                subscriptionInfo.ExpiryTime?.UtcDateTime,
                subscriptionInfo);

            await _subscriptionService.UpdateGooglePlayStorePriceAsync(
                request.PurchaseToken,
                request.FormattedPrice,
                string.IsNullOrWhiteSpace(request.PriceCurrencyCode) ? subscriptionInfo.PriceCurrencyCode : request.PriceCurrencyCode);

            if (!subscriptionInfo.IsAcknowledged)
            {
                await _verificationService.AcknowledgeSubscriptionAsync(request.PurchaseToken, productId);
            }

            var updatedSubscription = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(request.PurchaseToken);
            if (updatedSubscription != null)
            {
                if (shouldSendTrialStartedEmail)
                {
                    if (await _subscriptionConfirmationEmailService.SendTrialStartedAsync(user, updatedSubscription, GetBaseUrl()))
                    {
                        await _subscriptionService.MarkTrialActivationEmailSentAsync(updatedSubscription.Id);
                    }
                }
                else if (shouldSendTrialConvertedEmail)
                {
                    if (await _subscriptionConfirmationEmailService.SendTrialConvertedAsync(user, updatedSubscription, GetBaseUrl()))
                    {
                        await _subscriptionService.MarkTrialConversionEmailSentAsync(updatedSubscription.Id);
                    }
                }
                else if (shouldSendConfirmationEmail && !subscriptionInfo.IsFreeTrial)
                {
                    await _subscriptionConfirmationEmailService.SendConfirmationAsync(user, updatedSubscription, GetBaseUrl());
                }
            }

            return Ok(new { success = true, subscriptionId = existing.Id, status = existing.Status });
        }

        // Create new subscription record
        var subscription = await _subscriptionService.CreateGooglePlaySubscriptionAsync(
            user.Id, request.PurchaseToken, orderId, monthlyPrice, subscriptionInfo);

        await _subscriptionService.UpdateGooglePlaySubscriptionStatusAsync(
            request.PurchaseToken,
            localStatus,
            subscriptionInfo.ExpiryTime?.UtcDateTime,
            subscriptionInfo);

        await _subscriptionService.UpdateGooglePlayStorePriceAsync(
            request.PurchaseToken,
            request.FormattedPrice,
            string.IsNullOrWhiteSpace(request.PriceCurrencyCode) ? subscriptionInfo.PriceCurrencyCode : request.PriceCurrencyCode);

        // Acknowledge the purchase so Google doesn't auto-refund
        if (!subscriptionInfo.IsAcknowledged)
        {
            await _verificationService.AcknowledgeSubscriptionAsync(request.PurchaseToken, productId);
        }

        var refreshedSubscription = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(request.PurchaseToken)
            ?? subscription;

        if (subscriptionInfo.IsFreeTrial)
        {
            if (await _subscriptionConfirmationEmailService.SendTrialStartedAsync(user, refreshedSubscription, GetBaseUrl()))
            {
                await _subscriptionService.MarkTrialActivationEmailSentAsync(refreshedSubscription.Id);
            }
        }
        else
        {
            await _subscriptionConfirmationEmailService.SendConfirmationAsync(user, refreshedSubscription, GetBaseUrl());
        }

        return Ok(new { success = true, subscriptionId = subscription.Id, status = subscription.Status });
    }

    private static bool HasGooglePlayEntitlement(GooglePlaySubscriptionInfo subscriptionInfo)
    {
        if (subscriptionInfo.SubscriptionState == "SUBSCRIPTION_STATE_ACTIVE")
        {
            return true;
        }

        return subscriptionInfo.SubscriptionState == "SUBSCRIPTION_STATE_CANCELED"
            && subscriptionInfo.ExpiryTime?.UtcDateTime > DateTime.UtcNow;
    }

    private static string ResolveLocalStatus(GooglePlaySubscriptionInfo subscriptionInfo)
        => subscriptionInfo.SubscriptionState == "SUBSCRIPTION_STATE_CANCELED"
            ? SubscriptionStatuses.Cancelled
            : SubscriptionStatuses.Active;

    private (decimal MonthlyPrice, string PriceSource) ResolveMonthlyPrice(GooglePlaySubscriptionInfo subscriptionInfo, GooglePlayPurchaseRequest request)
    {
        if (subscriptionInfo.RecurringPrice.HasValue && subscriptionInfo.RecurringPrice.Value > 0)
        {
            return (subscriptionInfo.RecurringPrice.Value, "GooglePlayVerificationRecurringPrice");
        }

        if (request.PriceAmountMicros.HasValue && request.PriceAmountMicros.Value > 0)
        {
            return (Math.Round(request.PriceAmountMicros.Value / 1_000_000m, 2, MidpointRounding.AwayFromZero), "MauiBillingClientPriceAmountMicros");
        }

        if (decimal.TryParse(_configuration["AppSettings:SubscriptionPrice"], NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
        {
            return (price, "AppSettingsSubscriptionPrice");
        }

        return (3.99m, "HardcodedFallback");
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

public record GooglePlayPurchaseRequest(
    string PurchaseToken,
    string OrderId = "",
    string TimeZoneId = "",
    long? PriceAmountMicros = null,
    string PriceCurrencyCode = "",
    string FormattedPrice = "");
