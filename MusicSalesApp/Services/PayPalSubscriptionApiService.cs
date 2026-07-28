using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// OAuth-backed client for the PayPal Subscriptions REST API.
/// </summary>
public sealed class PayPalSubscriptionApiService : IPayPalSubscriptionApiService
{
    private const int PlansPageSize = 20;
    private const string DefaultApiBaseUrl = "https://api-m.sandbox.paypal.com/";
    private const string DefaultCancellationReason = "User requested cancellation";
    private const string HttpClientName = "PayPalSubscriptions";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PayPalSubscriptionApiService> _logger;

    public PayPalSubscriptionApiService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<PayPalSubscriptionApiService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PayPalPlan>> GetActivePlansAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var planIds = new List<string>();
        var knownPlanIds = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;

        while (true)
        {
            var path = $"v1/billing/plans?page_size={PlansPageSize}&page={page}&total_required=true";
            using var response = await SendApiRequestAsync(client, HttpMethod.Get, path, accessToken, null, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body, "list PayPal subscription plans");

            var pageResult = Deserialize<PayPalPlanListResponseDto>(body, "PayPal plan list");
            var summaries = pageResult.Plans ?? Array.Empty<PayPalPlanSummaryDto>();
            foreach (var summary in summaries)
            {
                if (string.IsNullOrWhiteSpace(summary.Id)
                    || (!string.IsNullOrWhiteSpace(summary.Status)
                        && !string.Equals(summary.Status, PayPalPlanStatuses.Active, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (knownPlanIds.Add(summary.Id))
                {
                    planIds.Add(summary.Id);
                }
            }

            var hasNextPage = pageResult.TotalPages.HasValue
                ? page < pageResult.TotalPages.Value
                : pageResult.Links?.Any(link => string.Equals(link.Rel, PayPalLinkRelations.Next, StringComparison.Ordinal)) == true;

            if (!hasNextPage)
            {
                break;
            }

            page++;
        }

        var activePlans = new List<PayPalPlan>(planIds.Count);
        foreach (var planId in planIds)
        {
            var plan = await GetPlanAsync(client, accessToken, planId, cancellationToken);
            if (plan != null && string.Equals(plan.Status, PayPalPlanStatuses.Active, StringComparison.Ordinal))
            {
                activePlans.Add(plan);
            }
        }

        return activePlans;
    }

    public async Task<PayPalPlan> GetPlanAsync(string planId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        return await GetPlanAsync(client, accessToken, planId, cancellationToken);
    }

    public async Task<PayPalSubscriptionDetails> GetSubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var escapedId = Uri.EscapeDataString(subscriptionId);
        using var response = await SendApiRequestAsync(
            client,
            HttpMethod.Get,
            $"v1/billing/subscriptions/{escapedId}?fields=plan",
            accessToken,
            null,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, body, $"retrieve PayPal subscription {subscriptionId}");
        var subscription = Deserialize<PayPalSubscriptionDetailsDto>(body, "PayPal subscription details");
        return MapSubscription(subscription);
    }

    public async Task<PayPalCreatedSubscription> CreateSubscriptionAsync(
        string planId,
        string returnUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(returnUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelUrl);

        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException("The PayPal return URL must be absolute.", nameof(returnUrl));
        }

        if (!Uri.TryCreate(cancelUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException("The PayPal cancel URL must be absolute.", nameof(cancelUrl));
        }

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var requestBody = new PayPalCreateSubscriptionRequestDto
        {
            PlanId = planId,
            ApplicationContext = new PayPalApplicationContextDto
            {
                BrandName = "StreamTunes",
                Locale = "en-US",
                LandingPage = "LOGIN",
                ShippingPreference = "NO_SHIPPING",
                UserAction = "SUBSCRIBE_NOW",
                ReturnUrl = returnUrl,
                CancelUrl = cancelUrl,
                PaymentMethod = new PayPalPaymentMethodDto
                {
                    PayerSelected = "PAYPAL",
                    PayeePreferred = "IMMEDIATE_PAYMENT_REQUIRED"
                }
            }
        };
        var json = JsonSerializer.Serialize(requestBody, JsonOptions);

        using var response = await SendApiRequestAsync(
            client,
            HttpMethod.Post,
            "v1/billing/subscriptions",
            accessToken,
            json,
            cancellationToken,
            preferRepresentation: true);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "create a PayPal subscription");

        var created = Deserialize<PayPalCreatedSubscriptionDto>(body, "PayPal subscription creation response");
        var approvalUrl = created.Links?
            .FirstOrDefault(link => string.Equals(link.Rel, PayPalLinkRelations.Approve, StringComparison.Ordinal))?
            .Href;

        if (string.IsNullOrWhiteSpace(created.Id) || string.IsNullOrWhiteSpace(approvalUrl))
        {
            throw new PayPalSubscriptionApiException("PayPal created the subscription but did not return both its ID and approval URL.");
        }

        return new PayPalCreatedSubscription(created.Id, approvalUrl);
    }

    public async Task<bool> CancelSubscriptionAsync(
        string subscriptionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var requestBody = JsonSerializer.Serialize(
            new PayPalCancelSubscriptionRequestDto
            {
                Reason = string.IsNullOrWhiteSpace(reason) ? DefaultCancellationReason : reason
            },
            JsonOptions);
        var escapedId = Uri.EscapeDataString(subscriptionId);

        using var response = await SendApiRequestAsync(
            client,
            HttpMethod.Post,
            $"v1/billing/subscriptions/{escapedId}/cancel",
            accessToken,
            requestBody,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound && IsResourceNotFound(body))
        {
            // Two different situations land here, and neither can ever be charged:
            //   1. The agreement genuinely no longer exists at PayPal.
            //   2. The agreement exists but was never approved by the buyer. PayPal answers 200 to a
            //      GET on an APPROVAL_PENDING subscription yet 404s this cancel call, because there
            //      is no active billing agreement to cancel. Verified against the sandbox on
            //      2026-07-28: GET returned 200/APPROVAL_PENDING and the cancel 404'd 500ms later.
            //
            // Do not narrow this branch without a replacement. Abandoned-checkout cleanup - both the
            // interactive cancel-return and the nightly sweep - reaches "cancelled" through case 2,
            // so reporting failure here would silently stop stale checkouts from ever being closed.
            _logger.LogInformation(
                "PayPal returned 404 cancelling subscription {SubscriptionId}; it is unapproved or already gone, so treating it as cancelled",
                subscriptionId);
            return true;
        }

        _logger.LogWarning(
            "PayPal rejected cancellation of subscription {SubscriptionId} with status {StatusCode}",
            subscriptionId,
            response.StatusCode);
        return false;
    }

    private async Task<PayPalPlan> GetPlanAsync(
        HttpClient client,
        string accessToken,
        string planId,
        CancellationToken cancellationToken)
    {
        var escapedId = Uri.EscapeDataString(planId);
        using var response = await SendApiRequestAsync(
            client,
            HttpMethod.Get,
            $"v1/billing/plans/{escapedId}",
            accessToken,
            null,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, body, $"retrieve PayPal plan {planId}");
        var plan = Deserialize<PayPalPlanDetailsDto>(body, "PayPal plan details");
        return MapPlan(plan, planId);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var clientId = _configuration["PayPal:ClientId"];
        var secret = _configuration["PayPal:Secret"];
        if (IsMissingCredential(clientId) || IsMissingCredential(secret))
        {
            throw new PayPalSubscriptionApiException("PayPal API credentials are not configured on the server.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUri("v1/oauth2/token"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });

        using var response = await SendHttpRequestAsync(
            client,
            request,
            "authenticate with PayPal",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "authenticate with PayPal");
        var tokenResponse = Deserialize<PayPalAccessTokenResponseDto>(body, "PayPal OAuth response");

        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            throw new PayPalSubscriptionApiException("PayPal authentication succeeded without returning an access token.");
        }

        return tokenResponse.AccessToken;
    }

    private async Task<HttpResponseMessage> SendApiRequestAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string accessToken,
        string json,
        CancellationToken cancellationToken,
        bool preferRepresentation = false)
    {
        using var request = new HttpRequestMessage(method, BuildApiUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (preferRepresentation)
        {
            request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        }

        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await SendHttpRequestAsync(
            client,
            request,
            "contact the PayPal subscriptions API",
            cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendHttpRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PayPalSubscriptionApiException(
                $"PayPal timed out while attempting to {operation}.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PayPalSubscriptionApiException(
                $"A network error occurred while attempting to {operation}.",
                ex);
        }
    }

    private Uri BuildApiUri(string path)
    {
        var configuredBaseUrl = _configuration["PayPal:ApiBaseUrl"];
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl) ? DefaultApiBaseUrl : configuredBaseUrl;
        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            baseUrl += "/";
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new PayPalSubscriptionApiException("PayPal:ApiBaseUrl must be an absolute URL.");
        }

        return new Uri(baseUri, path);
    }

    private static PayPalPlan MapPlan(PayPalPlanDetailsDto plan, string fallbackPlanId = null)
    {
        var cycles = plan.BillingCycles?
            .Select(MapBillingCycle)
            .OrderBy(cycle => cycle.Sequence)
            .ToArray()
            ?? Array.Empty<PayPalBillingCycle>();

        return new PayPalPlan
        {
            Id = string.IsNullOrWhiteSpace(plan.Id) ? fallbackPlanId : plan.Id,
            ProductId = plan.ProductId,
            Name = plan.Name,
            Description = plan.Description,
            Status = plan.Status,
            SetupFee = ParseOptionalDecimal(
                plan.PaymentPreferences?.SetupFee?.Value,
                "PayPal setup fee"),
            SetupFeeCurrencyCode = plan.PaymentPreferences?.SetupFee?.CurrencyCode,
            TaxPercentage = ParseOptionalDecimal(plan.Taxes?.Percentage, "PayPal tax percentage"),
            BillingCycles = cycles
        };
    }

    private static decimal ParseOptionalDecimal(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return decimal.Zero;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new PayPalSubscriptionApiException($"PayPal returned an invalid {fieldName}.");
    }

    private static PayPalBillingCycle MapBillingCycle(PayPalBillingCycleDto cycle)
    {
        decimal? fixedPrice = null;
        var value = cycle.PricingScheme?.FixedPrice?.Value;
        if (!string.IsNullOrWhiteSpace(value)
            && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPrice))
        {
            fixedPrice = parsedPrice;
        }
        else if (!string.IsNullOrWhiteSpace(value))
        {
            throw new PayPalSubscriptionApiException("PayPal returned a billing cycle with an invalid fixed price.");
        }

        return new PayPalBillingCycle
        {
            TenureType = cycle.TenureType,
            Sequence = cycle.Sequence,
            TotalCycles = cycle.TotalCycles,
            IntervalUnit = cycle.Frequency?.IntervalUnit,
            IntervalCount = cycle.Frequency?.IntervalCount ?? 0,
            FixedPrice = fixedPrice,
            CurrencyCode = cycle.PricingScheme?.FixedPrice?.CurrencyCode
        };
    }

    private static PayPalSubscriptionDetails MapSubscription(PayPalSubscriptionDetailsDto subscription)
    {
        var plan = subscription.Plan == null ? null : MapPlan(subscription.Plan, subscription.PlanId);
        var executions = subscription.BillingInfo?.CycleExecutions?
            .Select(execution => new PayPalBillingCycleExecution
            {
                TenureType = execution.TenureType,
                Sequence = execution.Sequence,
                CyclesCompleted = execution.CyclesCompleted,
                CyclesRemaining = execution.CyclesRemaining,
                TotalCycles = execution.TotalCycles
            })
            .OrderBy(execution => execution.Sequence)
            .ToArray()
            ?? Array.Empty<PayPalBillingCycleExecution>();

        var trialExecution = executions.FirstOrDefault(execution =>
            string.Equals(execution.TenureType, PayPalBillingTenureTypes.Trial, StringComparison.Ordinal));
        var regularExecution = executions.FirstOrDefault(execution =>
            string.Equals(execution.TenureType, PayPalBillingTenureTypes.Regular, StringComparison.Ordinal));
        var hasIncompleteTrialExecution = trialExecution != null
            && (trialExecution.CyclesRemaining > 0
                || (trialExecution.TotalCycles > 0 && trialExecution.CyclesCompleted < trialExecution.TotalCycles));
        var regularCycleHasNotStarted = regularExecution == null || regularExecution.CyclesCompleted == 0;
        var noExecutionFallback = trialExecution == null && subscription.BillingInfo?.LastPayment == null;
        var isInTrial = string.Equals(subscription.Status, SubscriptionStatuses.Active, StringComparison.Ordinal)
            && plan?.HasFreeTrial == true
            && regularCycleHasNotStarted
            && (hasIncompleteTrialExecution || noExecutionFallback);

        DateTimeOffset? trialEnd = null;
        var hasProviderTrialEvidence = trialExecution != null
            || subscription.BillingInfo?.NextBillingTime != null
            || subscription.BillingInfo?.LastPayment != null;
        if (plan?.TrialDays is int trialDays
            && subscription.StartTime.HasValue
            && (hasProviderTrialEvidence
                || string.Equals(subscription.Status, SubscriptionStatuses.Active, StringComparison.Ordinal)))
        {
            trialEnd = subscription.StartTime.Value.AddDays(trialDays);
        }

        if (isInTrial && subscription.BillingInfo?.NextBillingTime != null)
        {
            // While the trial is executing, PayPal's next billing timestamp is the provider-confirmed trial end.
            trialEnd = subscription.BillingInfo.NextBillingTime;
        }

        return new PayPalSubscriptionDetails
        {
            Id = subscription.Id,
            PlanId = subscription.PlanId,
            Status = subscription.Status,
            StartTime = subscription.StartTime,
            NextBillingTime = subscription.BillingInfo?.NextBillingTime,
            LastPaymentTime = subscription.BillingInfo?.LastPayment?.Time,
            FailedPaymentsCount = subscription.BillingInfo?.FailedPaymentsCount ?? 0,
            HasBillingInfo = subscription.BillingInfo != null,
            IsInTrial = isInTrial,
            TrialEndTime = trialEnd,
            Plan = plan,
            CycleExecutions = executions
        };
    }

    private static bool IsResourceNotFound(string body)
    {
        try
        {
            var error = JsonSerializer.Deserialize<PayPalApiErrorDto>(body, JsonOptions);
            return string.Equals(error?.Name, PayPalApiErrorNames.ResourceNotFound, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsMissingCredential(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            || value.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
    }

    private static T Deserialize<T>(string body, string responseName)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                ?? throw new PayPalSubscriptionApiException($"The {responseName} was empty.");
        }
        catch (JsonException ex)
        {
            throw new PayPalSubscriptionApiException($"The {responseName} was not valid JSON.", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string errorName = null;
        string errorMessage = null;
        try
        {
            var error = JsonSerializer.Deserialize<PayPalApiErrorDto>(body, JsonOptions);
            errorName = error?.Name;
            errorMessage = error?.Message;
        }
        catch (JsonException)
        {
        }

        var providerDetail = string.Join(
            ": ",
            new[] { errorName, errorMessage }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var suffix = string.IsNullOrWhiteSpace(providerDetail) ? string.Empty : $" PayPal reported: {providerDetail}.";
        throw new PayPalSubscriptionApiException(
            $"Unable to {operation} (HTTP {(int)response.StatusCode}).{suffix}");
    }

    private sealed class PayPalAccessTokenResponseDto
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; }
    }

    private sealed class PayPalPlanListResponseDto
    {
        [JsonPropertyName("plans")]
        public PayPalPlanSummaryDto[] Plans { get; init; }

        [JsonPropertyName("total_pages")]
        public int? TotalPages { get; init; }

        [JsonPropertyName("links")]
        public PayPalLinkDto[] Links { get; init; }
    }

    private sealed class PayPalPlanSummaryDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; }

        [JsonPropertyName("status")]
        public string Status { get; init; }
    }

    private sealed class PayPalPlanDetailsDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; }

        [JsonPropertyName("product_id")]
        public string ProductId { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; }

        [JsonPropertyName("description")]
        public string Description { get; init; }

        [JsonPropertyName("status")]
        public string Status { get; init; }

        [JsonPropertyName("billing_cycles")]
        public PayPalBillingCycleDto[] BillingCycles { get; init; }

        [JsonPropertyName("payment_preferences")]
        public PayPalPaymentPreferencesDto PaymentPreferences { get; init; }

        [JsonPropertyName("taxes")]
        public PayPalTaxesDto Taxes { get; init; }
    }

    private sealed class PayPalPaymentPreferencesDto
    {
        [JsonPropertyName("setup_fee")]
        public PayPalMoneyDto SetupFee { get; init; }
    }

    private sealed class PayPalTaxesDto
    {
        [JsonPropertyName("percentage")]
        public string Percentage { get; init; }
    }

    private sealed class PayPalBillingCycleDto
    {
        [JsonPropertyName("frequency")]
        public PayPalFrequencyDto Frequency { get; init; }

        [JsonPropertyName("tenure_type")]
        public string TenureType { get; init; }

        [JsonPropertyName("sequence")]
        public int Sequence { get; init; }

        [JsonPropertyName("total_cycles")]
        public int TotalCycles { get; init; }

        [JsonPropertyName("pricing_scheme")]
        public PayPalPricingSchemeDto PricingScheme { get; init; }
    }

    private sealed class PayPalFrequencyDto
    {
        [JsonPropertyName("interval_unit")]
        public string IntervalUnit { get; init; }

        [JsonPropertyName("interval_count")]
        public int IntervalCount { get; init; }
    }

    private sealed class PayPalPricingSchemeDto
    {
        [JsonPropertyName("fixed_price")]
        public PayPalMoneyDto FixedPrice { get; init; }
    }

    private sealed class PayPalMoneyDto
    {
        [JsonPropertyName("value")]
        public string Value { get; init; }

        [JsonPropertyName("currency_code")]
        public string CurrencyCode { get; init; }
    }

    private sealed class PayPalSubscriptionDetailsDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; }

        [JsonPropertyName("plan_id")]
        public string PlanId { get; init; }

        [JsonPropertyName("status")]
        public string Status { get; init; }

        [JsonPropertyName("start_time")]
        public DateTimeOffset? StartTime { get; init; }

        [JsonPropertyName("billing_info")]
        public PayPalBillingInfoDto BillingInfo { get; init; }

        [JsonPropertyName("plan")]
        public PayPalPlanDetailsDto Plan { get; init; }
    }

    private sealed class PayPalBillingInfoDto
    {
        [JsonPropertyName("next_billing_time")]
        public DateTimeOffset? NextBillingTime { get; init; }

        [JsonPropertyName("last_payment")]
        public PayPalLastPaymentDto LastPayment { get; init; }

        [JsonPropertyName("failed_payments_count")]
        public int FailedPaymentsCount { get; init; }

        [JsonPropertyName("cycle_executions")]
        public PayPalBillingCycleExecutionDto[] CycleExecutions { get; init; }
    }

    private sealed class PayPalLastPaymentDto
    {
        [JsonPropertyName("time")]
        public DateTimeOffset? Time { get; init; }
    }

    private sealed class PayPalBillingCycleExecutionDto
    {
        [JsonPropertyName("tenure_type")]
        public string TenureType { get; init; }

        [JsonPropertyName("sequence")]
        public int Sequence { get; init; }

        [JsonPropertyName("cycles_completed")]
        public int CyclesCompleted { get; init; }

        [JsonPropertyName("cycles_remaining")]
        public int CyclesRemaining { get; init; }

        [JsonPropertyName("total_cycles")]
        public int TotalCycles { get; init; }
    }

    private sealed class PayPalCreateSubscriptionRequestDto
    {
        [JsonPropertyName("plan_id")]
        public string PlanId { get; init; }

        [JsonPropertyName("application_context")]
        public PayPalApplicationContextDto ApplicationContext { get; init; }
    }

    private sealed class PayPalApplicationContextDto
    {
        [JsonPropertyName("brand_name")]
        public string BrandName { get; init; }

        [JsonPropertyName("locale")]
        public string Locale { get; init; }

        [JsonPropertyName("landing_page")]
        public string LandingPage { get; init; }

        [JsonPropertyName("shipping_preference")]
        public string ShippingPreference { get; init; }

        [JsonPropertyName("user_action")]
        public string UserAction { get; init; }

        [JsonPropertyName("return_url")]
        public string ReturnUrl { get; init; }

        [JsonPropertyName("cancel_url")]
        public string CancelUrl { get; init; }

        [JsonPropertyName("payment_method")]
        public PayPalPaymentMethodDto PaymentMethod { get; init; }
    }

    private sealed class PayPalPaymentMethodDto
    {
        [JsonPropertyName("payer_selected")]
        public string PayerSelected { get; init; }

        [JsonPropertyName("payee_preferred")]
        public string PayeePreferred { get; init; }
    }

    private sealed class PayPalCreatedSubscriptionDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; }

        [JsonPropertyName("links")]
        public PayPalLinkDto[] Links { get; init; }
    }

    private sealed class PayPalLinkDto
    {
        [JsonPropertyName("href")]
        public string Href { get; init; }

        [JsonPropertyName("rel")]
        public string Rel { get; init; }
    }

    private sealed class PayPalCancelSubscriptionRequestDto
    {
        [JsonPropertyName("reason")]
        public string Reason { get; init; }
    }

    private sealed class PayPalApiErrorDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; }
    }
}
