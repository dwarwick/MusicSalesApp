using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
[Authorize(Roles = "Admin,User")]
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
    private readonly IPurchaseEmailService _purchaseEmailService;

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
    public SubscriptionController(
        ISubscriptionService subscriptionService,
        IAppSettingsService appSettingsService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<SubscriptionController> logger,
        IHttpClientFactory httpClientFactory,
        IPurchaseEmailService purchaseEmailService)
    {
        _subscriptionService = subscriptionService;
        _appSettingsService = appSettingsService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _purchaseEmailService = purchaseEmailService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetSubscriptionStatus()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var subscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);
        
        if (subscription == null)
        {
            var subscriptionPrice = await _appSettingsService.GetSubscriptionPriceAsync();
            return Ok(new
            {
                hasSubscription = false,
                subscriptionPrice = subscriptionPrice.ToString("F2")
            });
        }

        return Ok(new
        {
            hasSubscription = true,
            status = subscription.Status,
            startDate = subscription.StartDate,
            endDate = subscription.EndDate,
            nextBillingDate = subscription.NextBillingDate,
            monthlyPrice = subscription.MonthlyPrice,
            paypalSubscriptionId = subscription.PayPalSubscriptionId
        });
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        // Create subscription plan with PayPal
        var planId = await GetOrCreateSubscriptionPlanAsync();
        if (string.IsNullOrEmpty(planId))
        {
            return BadRequest("Failed to create subscription plan");
        }

        // Create subscription with PayPal
        var subscriptionId = await CreatePayPalSubscriptionAsync(planId);
        if (string.IsNullOrEmpty(subscriptionId))
        {
            return BadRequest("Failed to create PayPal subscription");
        }

        // Save subscription to database - get price from database settings
        var monthlyPrice = await _appSettingsService.GetSubscriptionPriceAsync();
        var subscription = await _subscriptionService.CreateSubscriptionAsync(user.Id, subscriptionId, monthlyPrice);

        _logger.LogInformation("User {UserId} created subscription {SubscriptionId}", user.Id, subscription.Id);

        return Ok(new
        {
            success = true,
            subscriptionId = subscription.PayPalSubscriptionId,
            approvalUrl = await GetSubscriptionApprovalUrlAsync(subscriptionId)
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

        try
        {
            // Get subscription details from PayPal
            var subscriptionDetails = await GetPayPalSubscriptionDetailsAsync(request.SubscriptionId);
            if (subscriptionDetails == null)
            {
                return BadRequest("Failed to retrieve subscription details from PayPal");
            }

            // Update our database with the details
            await _subscriptionService.UpdateSubscriptionDetailsAsync(
                request.SubscriptionId,
                subscriptionDetails.NextBillingDate,
                subscriptionDetails.LastPaymentDate
            );

            _logger.LogInformation("Activated subscription {SubscriptionId} for user {UserId}", request.SubscriptionId, user.Id);

            // Send subscription confirmation email (fire and forget - don't block the response)
            var subscription = await _subscriptionService.GetSubscriptionByPayPalIdAsync(request.SubscriptionId);
            if (subscription != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var baseUrl = GetBaseUrl();
                        var userName = user.UserName ?? user.Email;
                        await _purchaseEmailService.SendSubscriptionConfirmationAsync(
                            user.Email,
                            userName,
                            subscription,
                            request.SubscriptionId,
                            baseUrl);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send subscription confirmation email to user {UserId}", user.Id);
                    }
                });
            }

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating subscription {SubscriptionId}", request.SubscriptionId);
            return StatusCode(500, "Failed to activate subscription");
        }
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

        try
        {
            // Get the user's most recent subscription
            var subscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);
            if (subscription == null)
            {
                return BadRequest("No subscription found for user");
            }

            // Get subscription details from PayPal
            var subscriptionDetails = await GetPayPalSubscriptionDetailsAsync(subscription.PayPalSubscriptionId);
            if (subscriptionDetails == null)
            {
                return BadRequest("Failed to retrieve subscription details from PayPal");
            }

            // Update our database with the details
            await _subscriptionService.UpdateSubscriptionDetailsAsync(
                subscription.PayPalSubscriptionId,
                subscriptionDetails.NextBillingDate,
                subscriptionDetails.LastPaymentDate
            );

            _logger.LogInformation("Activated subscription {SubscriptionId} for user {UserId}", subscription.PayPalSubscriptionId, user.Id);

            // Refresh subscription to get updated details
            var updatedSubscription = await _subscriptionService.GetSubscriptionByPayPalIdAsync(subscription.PayPalSubscriptionId);
            if (updatedSubscription != null)
            {
                // Send subscription confirmation email (fire and forget - don't block the response)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var baseUrl = GetBaseUrl();
                        var userName = user.UserName ?? user.Email;
                        await _purchaseEmailService.SendSubscriptionConfirmationAsync(
                            user.Email,
                            userName,
                            updatedSubscription,
                            subscription.PayPalSubscriptionId,
                            baseUrl);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send subscription confirmation email to user {UserId}", user.Id);
                    }
                });
            }

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating subscription for user {UserId}", user.Id);
            return StatusCode(500, "Failed to activate subscription");
        }
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> CancelSubscription()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var subscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);
        if (subscription == null)
        {
            return BadRequest("No active subscription found");
        }

        // Cancel with PayPal
        var cancelled = await CancelPayPalSubscriptionAsync(subscription.PayPalSubscriptionId);
        if (!cancelled)
        {
            _logger.LogWarning("Failed to cancel PayPal subscription {SubscriptionId}", subscription.PayPalSubscriptionId);
            return BadRequest("Failed to cancel PayPal subscription");
        }

        // Update local database
        await _subscriptionService.CancelSubscriptionAsync(user.Id);

        _logger.LogInformation("User {UserId} cancelled subscription", user.Id);

        // Get updated subscription to return end date
        var updatedSubscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);

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

        var deleted = await _subscriptionService.DeletePendingSubscriptionAsync(user.Id);

        if (deleted)
        {
            _logger.LogInformation("User {UserId} deleted pending subscription after PayPal cancellation", user.Id);
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
                    brand_name = "Music Sales App",
                    locale = "en-US",
                    shipping_preference = "NO_SHIPPING",
                    user_action = "SUBSCRIBE_NOW",
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
}

public class ActivateSubscriptionRequest
{
    public string SubscriptionId { get; set; }
}

public class PayPalSubscriptionDetails
{
    public DateTime? NextBillingDate { get; set; }
    public DateTime? LastPaymentDate { get; set; }
}
