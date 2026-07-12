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
/// API controller for managing user subscriptions and PayPal integration.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Admin,User", AuthenticationSchemes = "Identity.Application,Bearer")]
public class SubscriptionController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionController> _logger;
    /// <summary>
    /// HTTP client factory for making PayPal API requests.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISubscriptionConfirmationEmailService _subscriptionConfirmationEmailService;
    private readonly IAccountEmailService _accountEmailService;
    private readonly IGooglePlayVerificationService _googlePlayVerificationService;
    private readonly IPayPalSubscriptionManagementService _payPalSubscriptionManagementService;

    /// <summary>
    /// Initializes a new instance of the SubscriptionController.
    /// </summary>
    /// <param name="subscriptionService">Service for managing subscription business logic.</param>
    /// <param name="appSettingsService">Service for accessing application settings.</param>
    /// <param name="userManager">ASP.NET Identity user manager.</param>
    /// <param name="configuration">Application configuration for accessing PayPal settings.</param>
    /// <param name="logger">Logger for tracking subscription operations.</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients for PayPal API calls.</param>
    /// <param name="purchaseEmailService">Service for sending purchase confirmation emails.</param>
    /// <param name="accountEmailService">Service for sending account-related confirmation emails.</param>
    /// <param name="googlePlayVerificationService">Service for Google Play billing operations.</param>
    public SubscriptionController(
        ISubscriptionService subscriptionService,
        IAppSettingsService appSettingsService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<SubscriptionController> logger,
        IHttpClientFactory httpClientFactory,
        ISubscriptionConfirmationEmailService subscriptionConfirmationEmailService,
        IAccountEmailService accountEmailService,
        IGooglePlayVerificationService googlePlayVerificationService,
        IPayPalSubscriptionManagementService payPalSubscriptionManagementService)
    {
        _subscriptionService = subscriptionService;
        _appSettingsService = appSettingsService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _subscriptionConfirmationEmailService = subscriptionConfirmationEmailService;
        _accountEmailService = accountEmailService;
        _googlePlayVerificationService = googlePlayVerificationService;
        _payPalSubscriptionManagementService = payPalSubscriptionManagementService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetSubscriptionStatus()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        await _payPalSubscriptionManagementService.RefreshIfNeededAsync(user.Id, GetBaseUrl());

        var activeSubscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);
        var latestSubscription = activeSubscription ?? await _subscriptionService.GetLatestSubscriptionAsync(user.Id);
        _logger.LogInformation(
            "Subscription status request for user {UserId}: ActiveSubscriptionId={ActiveSubscriptionId}, ActiveStatus={ActiveStatus}, ActiveEndDate={ActiveEndDate}, LatestSubscriptionId={LatestSubscriptionId}, LatestStatus={LatestStatus}, LatestEndDate={LatestEndDate}, LatestTrialEndDate={LatestTrialEndDate}, BillingSource={BillingSource}",
            user.Id,
            activeSubscription?.Id,
            activeSubscription?.Status,
            activeSubscription?.EndDate,
            latestSubscription?.Id,
            latestSubscription?.Status,
            latestSubscription?.EndDate,
            latestSubscription?.TrialEndDate,
            latestSubscription?.BillingSource);

        if (await TryRefreshGooglePlaySubscriptionAsync(user, activeSubscription, latestSubscription))
        {
            activeSubscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);
            latestSubscription = activeSubscription ?? await _subscriptionService.GetLatestSubscriptionAsync(user.Id);
            _logger.LogInformation(
                "Subscription status after Google Play refresh for user {UserId}: ActiveSubscriptionId={ActiveSubscriptionId}, ActiveStatus={ActiveStatus}, ActiveEndDate={ActiveEndDate}, LatestSubscriptionId={LatestSubscriptionId}, LatestStatus={LatestStatus}, LatestEndDate={LatestEndDate}, LatestTrialEndDate={LatestTrialEndDate}",
                user.Id,
                activeSubscription?.Id,
                activeSubscription?.Status,
                activeSubscription?.EndDate,
                latestSubscription?.Id,
                latestSubscription?.Status,
                latestSubscription?.EndDate,
                latestSubscription?.TrialEndDate);
        }

        var subscriptionPrice = await _appSettingsService.GetSubscriptionPriceAsync();
        
        if (latestSubscription == null)
        {
            return Ok(new
            {
                hasSubscription = false,
                isOnTrial = false,
                subscriptionPrice = subscriptionPrice.ToString("F2"),
                isSubscriptionBlocked = user.IsSubscriptionBlocked
            });
        }

        var isOnTrial = latestSubscription.TrialEndDate.HasValue
            && latestSubscription.TrialEndDate.Value > DateTime.UtcNow
            && activeSubscription != null;

        return Ok(new
        {
            hasSubscription = activeSubscription != null,
            isOnTrial,
            status = latestSubscription.Status,
            startDate = latestSubscription.StartDate,
            endDate = latestSubscription.EndDate,
            nextBillingDate = latestSubscription.NextBillingDate,
            trialStartDate = latestSubscription.TrialStartDate,
            trialEndDate = latestSubscription.TrialEndDate,
            trialConvertedAt = latestSubscription.TrialConvertedAt,
            monthlyPrice = latestSubscription.MonthlyPrice,
            storeFormattedPrice = latestSubscription.StoreFormattedPrice,
            storePriceCurrencyCode = latestSubscription.StorePriceCurrencyCode,
            paypalSubscriptionId = latestSubscription.PayPalSubscriptionId,
            billingSource = latestSubscription.BillingSource,
            isSubscriptionBlocked = user.IsSubscriptionBlocked,
            subscriptionPrice = subscriptionPrice.ToString("F2")
        });
    }

    private async Task<bool> TryRefreshGooglePlaySubscriptionAsync(
        ApplicationUser user,
        Subscription activeSubscription,
        Subscription latestSubscription)
    {
        if (!ShouldRefreshGooglePlaySubscription(activeSubscription, latestSubscription))
        {
            return false;
        }

        var productId = _configuration["GooglePlay:SubscriptionProductId"] ?? "streamtunes_monthly_sub";
        GooglePlaySubscriptionInfo googlePlayInfo;
        try
        {
            _logger.LogInformation(
                "Refreshing Google Play subscription {SubscriptionId} for user {UserId}: LocalStatus={LocalStatus}, EndDate={EndDate}, TrialEndDate={TrialEndDate}, TrialConvertedAt={TrialConvertedAt}, AutoRenewEnabled={AutoRenewEnabled}",
                latestSubscription.Id,
                user.Id,
                latestSubscription.Status,
                latestSubscription.EndDate,
                latestSubscription.TrialEndDate,
                latestSubscription.TrialConvertedAt,
                latestSubscription.GooglePlayAutoRenewEnabled);

            googlePlayInfo = await _googlePlayVerificationService.VerifySubscriptionAsync(latestSubscription.GooglePlayPurchaseToken, productId);
        }
        catch (GooglePlayVerificationException ex)
        {
            _logger.LogWarning(ex, "Unable to refresh Google Play subscription {SubscriptionId} for user {UserId}", latestSubscription.Id, user.Id);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error refreshing Google Play subscription {SubscriptionId} for user {UserId}", latestSubscription.Id, user.Id);
            return false;
        }

        if (googlePlayInfo == null)
        {
            return false;
        }

        var previousTrialNeededConversionEmail = latestSubscription.TrialEndDate.HasValue
            && !latestSubscription.TrialConversionEmailSentAt.HasValue
            && !googlePlayInfo.IsFreeTrial;
        var refreshedStatus = ResolveGooglePlayLocalStatus(googlePlayInfo);

        _logger.LogInformation(
            "Google Play subscription refresh result for subscription {SubscriptionId}: GoogleState={GoogleState}, LocalStatus={LocalStatus}, IsFreeTrial={IsFreeTrial}, Expiry={ExpiryTime}, AutoRenewEnabled={AutoRenewEnabled}, RecurringPrice={RecurringPrice}, PriceCurrencyCode={PriceCurrencyCode}, PreviousTrialNeededConversionEmail={PreviousTrialNeededConversionEmail}",
            latestSubscription.Id,
            googlePlayInfo.SubscriptionState,
            refreshedStatus,
            googlePlayInfo.IsFreeTrial,
            googlePlayInfo.ExpiryTime,
            googlePlayInfo.AutoRenewEnabled,
            googlePlayInfo.RecurringPrice,
            googlePlayInfo.PriceCurrencyCode,
            previousTrialNeededConversionEmail);

        try
        {
            await _subscriptionService.UpdateGooglePlaySubscriptionStatusAsync(
                latestSubscription.GooglePlayPurchaseToken,
                refreshedStatus,
                googlePlayInfo.ExpiryTime?.UtcDateTime,
                googlePlayInfo);
        }
        catch (SubscriptionProviderConflictException ex)
        {
            _logger.LogWarning(
                ex,
                "Google Play refresh for user {UserId} was rejected because a current {ExistingBillingSource} subscription exists",
                user.Id,
                ex.ExistingBillingSource);
            return false;
        }

        if (!googlePlayInfo.IsAcknowledged)
        {
            await _googlePlayVerificationService.AcknowledgeSubscriptionAsync(latestSubscription.GooglePlayPurchaseToken, productId);
        }

        if (previousTrialNeededConversionEmail && HasGooglePlayEntitlement(googlePlayInfo))
        {
            var refreshed = await _subscriptionService.GetSubscriptionByGooglePlayTokenAsync(latestSubscription.GooglePlayPurchaseToken);
            if (refreshed != null && await _subscriptionConfirmationEmailService.SendTrialConvertedAsync(user, refreshed, GetBaseUrl()))
            {
                await _subscriptionService.MarkTrialConversionEmailSentAsync(refreshed.Id);
            }
        }

        return true;
    }

    private static bool ShouldRefreshGooglePlaySubscription(Subscription activeSubscription, Subscription latestSubscription)
    {
        if (latestSubscription == null || latestSubscription.BillingSource != BillingSources.GooglePlay || string.IsNullOrWhiteSpace(latestSubscription.GooglePlayPurchaseToken))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        return activeSubscription == null
            || (latestSubscription.TrialEndDate.HasValue
                && !latestSubscription.TrialConvertedAt.HasValue
                && latestSubscription.TrialEndDate.Value <= now.AddMinutes(5))
            || latestSubscription.EndDate <= now.AddMinutes(5);
    }

    private static string ResolveGooglePlayLocalStatus(GooglePlaySubscriptionInfo googlePlayInfo)
    {
        if (googlePlayInfo.SubscriptionState == "SUBSCRIPTION_STATE_CANCELED")
        {
            return SubscriptionStatuses.Cancelled;
        }

        return HasGooglePlayEntitlement(googlePlayInfo)
            ? SubscriptionStatuses.Active
            : SubscriptionStatuses.Expired;
    }

    private static bool HasGooglePlayEntitlement(GooglePlaySubscriptionInfo googlePlayInfo)
    {
        if (googlePlayInfo.SubscriptionState == "SUBSCRIPTION_STATE_ACTIVE")
        {
            return true;
        }

        return googlePlayInfo.SubscriptionState == "SUBSCRIPTION_STATE_CANCELED"
            && googlePlayInfo.ExpiryTime?.UtcDateTime > DateTime.UtcNow;
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var result = await _payPalSubscriptionManagementService.CreateSubscriptionAsync(
            user,
            request.AgreeToTerms,
            request.OfferVersion,
            request.PlanId,
            GetBaseUrl());
        if (!result.Success)
        {
            return BadRequest(result.Error);
        }

        return Ok(new
        {
            success = true,
            subscriptionId = result.SubscriptionId,
            approvalUrl = result.ApprovalUrl
        });
    }

    [HttpPost("activate")]
    public async Task<IActionResult> ActivateSubscription([FromBody] ActivateSubscriptionRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrEmpty(request.SubscriptionId))
        {
            return BadRequest("Subscription ID is required");
        }

        var localSubscription = await _subscriptionService.GetSubscriptionByPayPalIdAsync(request.SubscriptionId);
        if (localSubscription == null || localSubscription.UserId != user.Id)
        {
            return BadRequest("The PayPal subscription does not belong to the current user.");
        }

        var reconciled = await _payPalSubscriptionManagementService.ReconcileSubscriptionAsync(
            request.SubscriptionId,
            GetBaseUrl());
        if (reconciled == null)
        {
            return BadRequest("Failed to retrieve subscription details from PayPal.");
        }

        var subscription = reconciled.Subscription;
        var isTrial = subscription.TrialEndDate > DateTime.UtcNow && !subscription.LastPaymentDate.HasValue;
        if (!string.Equals(subscription.Status, SubscriptionStatuses.Active, StringComparison.Ordinal)
            || (!isTrial && !subscription.LastPaymentDate.HasValue))
        {
            return BadRequest($"Subscription is not active on PayPal. Current status: {subscription.Status}");
        }

        return Ok(new { success = true, isTrial });
    }

    private string GetBaseUrl()
    {
        // Use configured return URL if available, otherwise construct from request
        var returnBaseUrl = _configuration["PayPal:ReturnBaseUrl"];
        if (!string.IsNullOrEmpty(returnBaseUrl))
        {
            return returnBaseUrl;
        }
        return $"{Request.Scheme}://{Request.Host}";
    }

    [HttpPost("activate-current")]
    public async Task<IActionResult> ActivateCurrentUserSubscription()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var result = await _payPalSubscriptionManagementService.ActivateCurrentSubscriptionAsync(
            user,
            GetBaseUrl());
        if (!result.Success)
        {
            return BadRequest(result.Error);
        }

        return Ok(new { success = true, isTrial = result.IsTrial });
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> CancelSubscription()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var activeSubscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);
        var subscription = activeSubscription
            ?? await _subscriptionService.GetLatestSubscriptionAsync(user.Id);

        // A PayPal agreement can still be billable while its locally cached entitlement
        // has crossed an expiry boundary. Always let the provider-aware manager refresh
        // and cancel the latest PayPal agreement before rejecting it as inactive.
        if (subscription != null
            && string.Equals(subscription.BillingSource, BillingSources.PayPal, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(subscription.PayPalSubscriptionId))
        {
            var payPalResult = await _payPalSubscriptionManagementService.CancelSubscriptionAsync(
                user,
                GetBaseUrl());
            if (!payPalResult.Success)
            {
                return BadRequest(payPalResult.Error);
            }

            return Ok(new { success = true, endDate = payPalResult.EndDate });
        }

        if (activeSubscription == null)
        {
            return BadRequest("No active subscription found");
        }

        subscription = activeSubscription;

        if (string.Equals(subscription.BillingSource, BillingSources.Apple, StringComparison.Ordinal))
        {
            return BadRequest("Apple subscriptions must be cancelled in Apple Subscriptions settings.");
        }

        // Route cancellation to the correct billing provider
        var cancelled = await CancelWithProviderAsync(subscription);
        if (!cancelled)
        {
            return BadRequest($"Failed to cancel subscription with {subscription.BillingSource}");
        }

        // Google Play's verified EndDate is the current entitlement expiry for both
        // trials and converted paid subscriptions. A historical TrialEndDate must not
        // shorten a converted subscriber's paid-through access.
        var providerEntitlementEnd = subscription.EndDate
            ?? subscription.NextBillingDate
            ?? subscription.TrialEndDate;
        await _subscriptionService.CancelSubscriptionAsync(user.Id, providerEntitlementEnd);

        _logger.LogInformation("User {UserId} cancelled subscription", user.Id);

        // Get updated subscription to return end date
        var updatedSubscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);

        var userEmail = user.Email;
        if (!string.IsNullOrEmpty(userEmail))
        {
            try
            {
                await _accountEmailService.SendSubscriptionCancelledEmailAsync(
                    userEmail,
                    user.UserName ?? userEmail,
                    updatedSubscription?.EndDate,
                    updatedSubscription?.BillingSource,
                    user.TimeZoneId,
                    GetBaseUrl());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send subscription cancellation email to user {UserId}", user.Id);
            }
        }

        return Ok(new
        {
            success = true,
            endDate = updatedSubscription?.EndDate
        });
    }

    [HttpPost("delete-pending")]
    public async Task<IActionResult> DeletePendingSubscription()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var deleted = await _payPalSubscriptionManagementService.AbandonPendingCheckoutAsync(user);

        if (deleted)
        {
            _logger.LogInformation("User {UserId} abandoned pending PayPal checkout", user.Id);
            return Ok(new { success = true });
        }
        
        return Ok(new { success = false, message = "No pending subscription found" });
    }


    private async Task<string> GetOrCreateSubscriptionPlanAsync()
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogError("Unable to retrieve PayPal access token");
                return null;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            
            // Get subscription price from database
            var monthlyPrice = await _appSettingsService.GetSubscriptionPriceAsync();
            var monthlyPriceStr = monthlyPrice.ToString("F2");

            // First, try to find an existing plan with matching price
            var existingPlan = await FindExistingPlanAsync(token, baseUrl, monthlyPriceStr);
            if (!string.IsNullOrEmpty(existingPlan))
            {
                _logger.LogInformation("Using existing PayPal plan: {PlanId}", existingPlan);
                return existingPlan;
            }

            // If no plan exists, create product first, then plan
            var productId = await GetOrCreateProductAsync(token, baseUrl);
            if (string.IsNullOrEmpty(productId))
            {
                _logger.LogError("Failed to create or find product");
                return null;
            }

            // Now create the plan with the product ID - include price in plan name
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Prefer", "return=representation");

            var planName = $"Music Streaming Monthly Subscription - ${monthlyPriceStr}";

            var planData = new
            {
                product_id = productId,
                name = planName,
                description = $"Unlimited music streaming for ${monthlyPriceStr} per month",
                status = "ACTIVE",
                billing_cycles = new[]
                {
                    new
                    {
                        frequency = new
                        {
                            interval_unit = "MONTH",
                            interval_count = 1
                        },
                        tenure_type = "REGULAR",
                        sequence = 1,
                        total_cycles = 0,
                        pricing_scheme = new
                        {
                            fixed_price = new
                            {
                                value = monthlyPriceStr,
                                currency_code = "USD"
                            }
                        }
                    }
                },
                payment_preferences = new
                {
                    auto_bill_outstanding = true,
                    setup_fee_failure_action = "CONTINUE",
                    payment_failure_threshold = 3
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(planData), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("v1/billing/plans", content);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var planId = doc.RootElement.GetProperty("id").GetString();
                _logger.LogInformation("Created new PayPal plan: {PlanId} with price ${Price}", planId, monthlyPriceStr);
                return planId;
            }
            else
            {
                _logger.LogError("Failed to create subscription plan: {Status} {Body}", response.StatusCode, body);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating subscription plan");
            return null;
        }
    }

    private async Task<string> GetOrCreateProductAsync(string token, string baseUrl)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Prefer", "return=representation");

            // Try to find existing product first
            var response = await client.GetAsync("v1/catalogs/products?page_size=20");
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("products", out var products))
                {
                    foreach (var product in products.EnumerateArray())
                    {
                        if (product.TryGetProperty("name", out var name) && 
                            name.GetString().Contains("Music Streaming"))
                        {
                            var productId = product.GetProperty("id").GetString();
                            _logger.LogInformation("Found existing PayPal product: {ProductId}", productId);
                            return productId;
                        }
                    }
                }
            }

            // Create new product if not found
            var productData = new
            {
                name = "Music Streaming Subscription",
                description = "Unlimited music streaming subscription service",
                type = "SERVICE",
                category = "SOFTWARE"
            };

            var productContent = new StringContent(JsonSerializer.Serialize(productData), Encoding.UTF8, "application/json");
            var createResponse = await client.PostAsync("v1/catalogs/products", productContent);
            var createBody = await createResponse.Content.ReadAsStringAsync();

            if (createResponse.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(createBody);
                var productId = doc.RootElement.GetProperty("id").GetString();
                _logger.LogInformation("Created new PayPal product: {ProductId}", productId);
                return productId;
            }
            else
            {
                _logger.LogError("Failed to create product: {Status} {Body}", createResponse.StatusCode, createBody);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting or creating product");
            return null;
        }
    }

    private async Task<string> FindExistingPlanAsync(string token, string baseUrl, string monthlyPrice)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync("v1/billing/plans?page_size=20");
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("plans", out var plans))
                {
                    // Build the expected plan name including price
                    var expectedPlanName = $"Music Streaming Monthly Subscription - ${monthlyPrice}";
                    
                    foreach (var plan in plans.EnumerateArray())
                    {
                        if (plan.TryGetProperty("name", out var name))
                        {
                            var planName = name.GetString();
                            // Match exact plan name including price
                            if (planName == expectedPlanName)
                            {
                                _logger.LogInformation("Found existing plan with matching price: {PlanName}", planName);
                                return plan.GetProperty("id").GetString();
                            }
                        }
                    }
                    
                    _logger.LogInformation("No existing plan found for price ${Price}", monthlyPrice);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding existing plan");
        }

        return null;
    }

    private async Task<string> CreatePayPalSubscriptionAsync(string planId)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Prefer", "return=representation");

            // Use configured base URL instead of Request headers to prevent Host Header Injection
            var returnBaseUrl = _configuration["PayPal:ReturnBaseUrl"];
            if (string.IsNullOrEmpty(returnBaseUrl))
            {
                // Fallback to request if not configured (for development)
                returnBaseUrl = $"{Request.Scheme}://{Request.Host}";
                _logger.LogWarning("PayPal:ReturnBaseUrl not configured, using request headers as fallback");
            }

            var subscriptionData = new
            {
                plan_id = planId,
                start_time = DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                application_context = new
                {
                    brand_name = "StreamTunes",
                    locale = "en-US",
                    landing_page = "LOGIN",
                    shipping_preference = "NO_SHIPPING",
                    user_action = "SUBSCRIBE_NOW",
                    payment_method = new
                    {
                        payer_selected = "PAYPAL",
                        payee_preferred = "IMMEDIATE_PAYMENT_REQUIRED"
                    },
                    return_url = $"{returnBaseUrl}/manage-account?success=true",
                    cancel_url = $"{returnBaseUrl}/manage-account?success=false"
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(subscriptionData), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("v1/billing/subscriptions", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to create PayPal subscription: {Status} {Body}", response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating PayPal subscription");
            return null;
        }
    }

    private async Task<string> GetSubscriptionApprovalUrlAsync(string subscriptionId)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync($"v1/billing/subscriptions/{subscriptionId}");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("links", out var links))
            {
                foreach (var link in links.EnumerateArray())
                {
                    if (link.TryGetProperty("rel", out var rel) && rel.GetString() == "approve")
                    {
                        return link.GetProperty("href").GetString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting subscription approval URL");
        }

        return null;
    }

    /// <summary>
    /// Routes subscription cancellation to the correct billing provider based on BillingSource.
    /// </summary>
    private async Task<bool> CancelWithProviderAsync(Subscription subscription)
    {
        switch (subscription.BillingSource)
        {
            case BillingSources.GooglePlay:
                var productId = _configuration["GooglePlay:SubscriptionProductId"] ?? "streamtunes_monthly_sub";
                var gpCancelled = await _googlePlayVerificationService.CancelSubscriptionAsync(
                    subscription.GooglePlayPurchaseToken, productId);
                if (!gpCancelled)
                    _logger.LogWarning("Failed to cancel Google Play subscription for user {UserId}", subscription.UserId);
                return gpCancelled;

            case BillingSources.Apple:
                _logger.LogWarning("Attempted server-side cancellation for Apple subscription owned by user {UserId}", subscription.UserId);
                return false;

            case BillingSources.PayPal:
            default:
                var ppCancelled = await CancelPayPalSubscriptionAsync(subscription.PayPalSubscriptionId);
                if (!ppCancelled)
                    _logger.LogWarning("Failed to cancel PayPal subscription {SubscriptionId}", subscription.PayPalSubscriptionId);
                return ppCancelled;
        }
    }

    private async Task<bool> CancelPayPalSubscriptionAsync(string subscriptionId)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var cancelData = new
            {
                reason = "User requested cancellation"
            };

            var content = new StringContent(JsonSerializer.Serialize(cancelData), Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"v1/billing/subscriptions/{subscriptionId}/cancel", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to cancel PayPal subscription: {Status} {Body}", response.StatusCode, body);
                
                // If PayPal returns 404 RESOURCE_NOT_FOUND, the subscription is already cancelled
                // or doesn't exist on PayPal's side. We should still allow local cancellation.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("name", out var nameElement) && 
                            nameElement.ValueKind == JsonValueKind.String &&
                            nameElement.GetString() == "RESOURCE_NOT_FOUND")
                        {
                            _logger.LogInformation("PayPal subscription {SubscriptionId} not found - treating as already cancelled", subscriptionId);
                            return true;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse PayPal error response for subscription {SubscriptionId}", subscriptionId);
                    }
                }
                
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling PayPal subscription");
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
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });
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

    private async Task<PayPalSubscriptionDetails> GetPayPalSubscriptionDetailsAsync(string subscriptionId)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync($"v1/billing/subscriptions/{subscriptionId}");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get subscription details: {Status} {Body}", response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var details = new PayPalSubscriptionDetails();

            // Get subscription status from PayPal
            if (root.TryGetProperty("status", out var statusProp))
            {
                details.Status = statusProp.GetString();
            }

            // Get billing info
            if (root.TryGetProperty("billing_info", out var billingInfo))
            {
                if (billingInfo.TryGetProperty("next_billing_time", out var nextBillingTime))
                {
                    if (DateTime.TryParse(nextBillingTime.GetString(), out var nextBilling))
                    {
                        details.NextBillingDate = nextBilling;
                    }
                }

                if (billingInfo.TryGetProperty("last_payment", out var lastPayment))
                {
                    if (lastPayment.TryGetProperty("time", out var lastPaymentTime))
                    {
                        if (DateTime.TryParse(lastPaymentTime.GetString(), out var lastPaymentDate))
                        {
                            details.LastPaymentDate = lastPaymentDate;
                        }
                    }
                }
            }

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting PayPal subscription details");
            return null;
        }
    }
}

public class CreateSubscriptionRequest
{
    public bool AgreeToTerms { get; set; }
    public int? OfferVersion { get; set; }
    public string PlanId { get; set; }
}

public class ActivateSubscriptionRequest
{
    public string SubscriptionId { get; set; }
}

public class PayPalSubscriptionDetails
{
    public string Status { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public DateTime? LastPaymentDate { get; set; }
}
