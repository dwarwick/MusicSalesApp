#nullable enable
using MusicSalesApp.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for PayPal Partner Referrals API operations.
/// Handles seller onboarding, multi-party payments, and refunds.
/// </summary>
public class PayPalPartnerService : IPayPalPartnerService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PayPalPartnerService> _logger;

    public PayPalPartnerService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PayPalPartnerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PartnerReferralResult?> CreatePartnerReferralAsync(int userId, string email)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new PartnerReferralResult { Success = false, ErrorMessage = "Failed to get PayPal access token" };
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var returnBaseUrl = _configuration["PayPal:ReturnBaseUrl"] ?? "https://localhost:5001";
            var bnCode = _configuration["PayPal:BNCode"];

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Prefer", "return=representation");
            
            // Add Partner Attribution ID (BN Code) if configured
            if (!string.IsNullOrWhiteSpace(bnCode))
            {
                client.DefaultRequestHeaders.Add("PayPal-Partner-Attribution-Id", bnCode);
            }

            // Generate a unique tracking ID for this onboarding
            var trackingId = $"SELLER-{userId}-{DateTime.UtcNow.Ticks}";

            var referralData = new
            {
                tracking_id = trackingId,
                partner_config_override = new
                {
                    partner_logo_url = $"{returnBaseUrl}/images/logo-light-small.png",
                    return_url = $"{returnBaseUrl}/manage-account?seller_onboarding=complete&tracking_id={trackingId}",
                    return_url_description = "Return to StreamTunes to complete your seller setup.",
                    action_renewal_url = $"{returnBaseUrl}/manage-account?seller_onboarding=renew&tracking_id={trackingId}",
                    show_add_credit_card = true
                },
                operations = new[]
                {
                    new
                    {
                        operation = "API_INTEGRATION",
                        api_integration_preference = new
                        {
                            rest_api_integration = new
                            {
                                integration_method = "PAYPAL",
                                integration_type = "THIRD_PARTY",
                                third_party_details = new
                                {
                                    features = new[] { "PAYMENT", "REFUND" }
                                }
                            }
                        }
                    }
                },
                products = new[] { "EXPRESS_CHECKOUT" },
                legal_consents = new[]
                {
                    new
                    {
                        type = "SHARE_DATA_CONSENT",
                        granted = true
                    }
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(referralData), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("v2/customer/partner-referrals", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to create partner referral: {Status} {Body}", response.StatusCode, body);
                return new PartnerReferralResult { Success = false, ErrorMessage = $"PayPal API error: {response.StatusCode}" };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Find the action_url link for seller onboarding
            string? referralUrl = null;
            if (root.TryGetProperty("links", out var links))
            {
                foreach (var link in links.EnumerateArray())
                {
                    if (link.TryGetProperty("rel", out var rel) && rel.GetString() == "action_url")
                    {
                        referralUrl = link.GetProperty("href").GetString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(referralUrl))
            {
                _logger.LogError("No action_url found in partner referral response: {Body}", body);
                return new PartnerReferralResult { Success = false, ErrorMessage = "No referral URL returned from PayPal" };
            }

            _logger.LogInformation("Created partner referral for user {UserId} with tracking ID {TrackingId}", userId, trackingId);

            return new PartnerReferralResult
            {
                Success = true,
                ReferralUrl = referralUrl,
                TrackingId = trackingId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating partner referral for user {UserId}", userId);
            return new PartnerReferralResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<MerchantOnboardingStatus?> GetMerchantStatusAsync(string partnerId, string merchantId)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var bnCode = _configuration["PayPal:BNCode"];

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            if (!string.IsNullOrWhiteSpace(bnCode))
            {
                client.DefaultRequestHeaders.Add("PayPal-Partner-Attribution-Id", bnCode);
            }

            var response = await client.GetAsync($"v1/customer/partners/{partnerId}/merchant-integrations/{merchantId}");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get merchant status: {Status} {Body}", response.StatusCode, body);
                return null;
            }

            return ParseMerchantStatus(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting merchant status for {MerchantId}", merchantId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<MerchantOnboardingStatus?> GetMerchantStatusByTrackingIdAsync(string trackingId)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var partnerId = _configuration["PayPal:PartnerId"];
            var bnCode = _configuration["PayPal:BNCode"];

            if (string.IsNullOrWhiteSpace(partnerId))
            {
                _logger.LogError("PayPal:PartnerId not configured");
                return null;
            }

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            if (!string.IsNullOrWhiteSpace(bnCode))
            {
                client.DefaultRequestHeaders.Add("PayPal-Partner-Attribution-Id", bnCode);
            }

            var response = await client.GetAsync($"v1/customer/partners/{partnerId}/merchant-integrations?tracking_id={trackingId}");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get merchant status by tracking ID: {Status} {Body}", response.StatusCode, body);
                return null;
            }

            return ParseMerchantStatus(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting merchant status by tracking ID {TrackingId}", trackingId);
            return null;
        }
    }

    private MerchantOnboardingStatus ParseMerchantStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var status = new MerchantOnboardingStatus();

        if (root.TryGetProperty("merchant_id", out var merchantId))
        {
            status.MerchantId = merchantId.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("tracking_id", out var trackingId))
        {
            status.TrackingId = trackingId.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("payments_receivable", out var paymentsReceivable))
        {
            status.PaymentsReceivable = paymentsReceivable.GetBoolean();
        }

        if (root.TryGetProperty("primary_email_confirmed", out var primaryEmailConfirmed))
        {
            status.PrimaryEmailConfirmed = primaryEmailConfirmed.GetBoolean();
        }

        if (root.TryGetProperty("products", out var products))
        {
            foreach (var product in products.EnumerateArray())
            {
                if (product.TryGetProperty("name", out var name))
                {
                    status.Products.Add(name.GetString() ?? string.Empty);
                }
            }
        }

        if (root.TryGetProperty("oauth_integrations", out var oauthIntegrations))
        {
            foreach (var integration in oauthIntegrations.EnumerateArray())
            {
                var oauthIntegration = new OAuthIntegration();
                
                if (integration.TryGetProperty("integration_type", out var integrationType))
                {
                    oauthIntegration.IntegrationType = integrationType.GetString() ?? string.Empty;
                }
                
                if (integration.TryGetProperty("integration_method", out var integrationMethod))
                {
                    oauthIntegration.IntegrationMethod = integrationMethod.GetString() ?? string.Empty;
                }
                
                if (integration.TryGetProperty("status", out var integrationStatus))
                {
                    oauthIntegration.Status = integrationStatus.GetString() ?? string.Empty;
                }

                status.OAuthIntegrations.Add(oauthIntegration);
            }
        }

        return status;
    }

    /// <inheritdoc />
    public async Task<RefundResult> IssueRefundAsync(string captureId, decimal? amount = null, string? reason = null)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new RefundResult { Success = false, ErrorMessage = "Failed to get PayPal access token" };
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var bnCode = _configuration["PayPal:BNCode"];

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Prefer", "return=representation");
            
            if (!string.IsNullOrWhiteSpace(bnCode))
            {
                client.DefaultRequestHeaders.Add("PayPal-Partner-Attribution-Id", bnCode);
            }

            object refundData;
            if (amount.HasValue)
            {
                refundData = new
                {
                    amount = new
                    {
                        value = amount.Value.ToString("F2"),
                        currency_code = "USD"
                    },
                    note_to_payer = reason ?? "Refund from StreamTunes"
                };
            }
            else
            {
                refundData = new
                {
                    note_to_payer = reason ?? "Refund from StreamTunes"
                };
            }

            var content = new StringContent(JsonSerializer.Serialize(refundData), Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"v2/payments/captures/{captureId}/refund", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to issue refund: {Status} {Body}", response.StatusCode, body);
                return new RefundResult { Success = false, ErrorMessage = $"Refund failed: {response.StatusCode}" };
            }

            using var doc = JsonDocument.Parse(body);
            var refundId = doc.RootElement.GetProperty("id").GetString();

            _logger.LogInformation("Issued refund {RefundId} for capture {CaptureId}", refundId, captureId);

            return new RefundResult { Success = true, RefundId = refundId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error issuing refund for capture {CaptureId}", captureId);
            return new RefundResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<MultiPartyOrderResult?> CreateMultiPartyOrderAsync(Seller seller, IEnumerable<OrderItem> items, decimal totalAmount, decimal platformFee)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(seller.PayPalMerchantId))
            {
                return new MultiPartyOrderResult { Success = false, ErrorMessage = "Seller does not have a PayPal merchant ID" };
            }

            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new MultiPartyOrderResult { Success = false, ErrorMessage = "Failed to get PayPal access token" };
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var returnBaseUrl = _configuration["PayPal:ReturnBaseUrl"] ?? "https://localhost:5001";
            var bnCode = _configuration["PayPal:BNCode"];

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Prefer", "return=representation");
            
            if (!string.IsNullOrWhiteSpace(bnCode))
            {
                client.DefaultRequestHeaders.Add("PayPal-Partner-Attribution-Id", bnCode);
            }

            var orderItems = items.Select(item => new
            {
                name = item.Name,
                unit_amount = new
                {
                    currency_code = "USD",
                    value = item.UnitAmount.ToString("F2")
                },
                quantity = item.Quantity.ToString()
            }).ToArray();

            var orderData = new
            {
                intent = "CAPTURE",
                purchase_units = new[]
                {
                    new
                    {
                        amount = new
                        {
                            currency_code = "USD",
                            value = totalAmount.ToString("F2"),
                            breakdown = new
                            {
                                item_total = new
                                {
                                    currency_code = "USD",
                                    value = totalAmount.ToString("F2")
                                }
                            }
                        },
                        payee = new
                        {
                            merchant_id = seller.PayPalMerchantId
                        },
                        payment_instruction = new
                        {
                            disbursement_mode = "INSTANT",
                            platform_fees = new[]
                            {
                                new
                                {
                                    amount = new
                                    {
                                        currency_code = "USD",
                                        value = platformFee.ToString("F2")
                                    }
                                }
                            }
                        },
                        items = orderItems
                    }
                },
                application_context = new
                {
                    brand_name = "StreamTunes",
                    landing_page = "NO_PREFERENCE",
                    shipping_preference = "NO_SHIPPING",
                    user_action = "PAY_NOW",
                    return_url = $"{returnBaseUrl}/checkout?success=true",
                    cancel_url = $"{returnBaseUrl}/checkout?success=false"
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(orderData), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("v2/checkout/orders", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to create multi-party order: {Status} {Body}", response.StatusCode, body);
                return new MultiPartyOrderResult { Success = false, ErrorMessage = $"Order creation failed: {response.StatusCode}" };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var orderId = root.GetProperty("id").GetString() ?? string.Empty;

            // Find the approval URL
            string? approvalUrl = null;
            if (root.TryGetProperty("links", out var links))
            {
                foreach (var link in links.EnumerateArray())
                {
                    if (link.TryGetProperty("rel", out var rel) && rel.GetString() == "approve")
                    {
                        approvalUrl = link.GetProperty("href").GetString();
                        break;
                    }
                }
            }

            _logger.LogInformation("Created multi-party order {OrderId} for seller {SellerId}", orderId, seller.Id);

            return new MultiPartyOrderResult
            {
                Success = true,
                OrderId = orderId,
                ApprovalUrl = approvalUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating multi-party order for seller {SellerId}", seller.Id);
            return new MultiPartyOrderResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<CaptureResult> CaptureMultiPartyOrderAsync(string payPalOrderId)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new CaptureResult { Success = false, ErrorMessage = "Failed to get PayPal access token" };
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var bnCode = _configuration["PayPal:BNCode"];

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Prefer", "return=representation");
            
            if (!string.IsNullOrWhiteSpace(bnCode))
            {
                client.DefaultRequestHeaders.Add("PayPal-Partner-Attribution-Id", bnCode);
            }

            var response = await client.PostAsync($"v2/checkout/orders/{payPalOrderId}/capture", new StringContent("{}", Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to capture multi-party order: {Status} {Body}", response.StatusCode, body);
                return new CaptureResult { Success = false, ErrorMessage = $"Capture failed: {response.StatusCode}" };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString();

            if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Multi-party order capture returned status {Status}", status);
                return new CaptureResult { Success = false, ErrorMessage = $"Payment is in {status} status" };
            }

            // Get capture ID from the response
            string? captureId = null;
            if (root.TryGetProperty("purchase_units", out var purchaseUnits))
            {
                foreach (var unit in purchaseUnits.EnumerateArray())
                {
                    if (unit.TryGetProperty("payments", out var payments) && 
                        payments.TryGetProperty("captures", out var captures))
                    {
                        foreach (var capture in captures.EnumerateArray())
                        {
                            captureId = capture.GetProperty("id").GetString();
                            break;
                        }
                    }
                }
            }

            _logger.LogInformation("Captured multi-party order {OrderId}, capture ID: {CaptureId}", payPalOrderId, captureId);

            return new CaptureResult { Success = true, CaptureId = captureId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error capturing multi-party order {OrderId}", payPalOrderId);
            return new CaptureResult { Success = false, ErrorMessage = ex.Message };
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
}
