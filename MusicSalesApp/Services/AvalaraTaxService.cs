#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for Avalara OAuth authentication using client credentials flow.
/// </summary>
public sealed class AvalaraTaxService : IAvalaraTaxService
{
    private readonly HttpClient _http;
    private readonly ILogger<AvalaraTaxService> _logger;
    private readonly IConfiguration _configuration;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AvalaraTaxService(
        HttpClient http,
        ILogger<AvalaraTaxService> logger,
        IConfiguration configuration)
    {
        _http = http;
        _logger = logger;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public async Task<AvalaraAuthResponse> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var clientId = _configuration["Avalara:ClientId"];
        var clientSecret = _configuration["Avalara:ClientSecret"];
        var useSandbox = _configuration.GetValue<bool>("Avalara:UseSandbox", true);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            var errorMsg = "Avalara configuration is incomplete. Please check ClientId and ClientSecret.";
            _logger.LogError("{Error}", errorMsg);
            return new AvalaraAuthResponse
            {
                Success = false,
                ErrorMessage = errorMsg
            };
        }

        return await GetAccessTokenAsync(clientId, clientSecret, useSandbox, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AvalaraAuthResponse> GetAccessTokenAsync(
        string clientId,
        string clientSecret,
        bool useSandbox = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("clientId is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("clientSecret is required.", nameof(clientSecret));

        _logger.LogInformation("Requesting Avalara access token for clientId: {ClientId}", clientId);

        var response = new AvalaraAuthResponse();

        try
        {
            // Get URL from configuration or use defaults
            var url = useSandbox
                ? _configuration["Avalara:SandboxTokenUrl"] ?? "https://ai-sbx.avlr.sh/connect/token"
                : _configuration["Avalara:ProductionTokenUrl"] ?? "https://ai.avlr.sh/connect/token";

            // Build the form data for client_credentials grant
            var formData = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            };

            using var content = new FormUrlEncodedContent(formData);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

            using var httpResponse = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            response.RawResponse = body;

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorMsg = $"Avalara auth failed: {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}";
                _logger.LogError("{Error}. Body: {Body}", errorMsg, body);
                response.Success = false;
                response.ErrorMessage = errorMsg;
                return response;
            }

            // Parse the OAuth response
            var tokenResponse = JsonSerializer.Deserialize<AvalaraTokenResponse>(body, JsonOptions);

            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                var errorMsg = "Failed to deserialize Avalara auth response or access token is missing.";
                _logger.LogError("{Error}", errorMsg);
                response.Success = false;
                response.ErrorMessage = errorMsg;
                return response;
            }

            response.Success = true;
            response.AccessToken = tokenResponse.AccessToken;
            response.TokenType = tokenResponse.TokenType ?? "Bearer";
            response.ExpiresIn = tokenResponse.ExpiresIn;

            _logger.LogInformation("Avalara access token obtained successfully. Expires in {ExpiresIn} seconds.", response.ExpiresIn);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obtaining Avalara access token");
            response.Success = false;
            response.ErrorMessage = $"Exception during Avalara authentication: {ex.Message}";
            return response;
        }
    }

    /// <inheritdoc />
    public async Task<AvalaraFormRequestResponse> CreateFormRequestAsync(
        string formType,
        string referenceId,
        int ttl = 3600,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formType))
            throw new ArgumentException("formType is required.", nameof(formType));
        if (string.IsNullOrWhiteSpace(referenceId))
            throw new ArgumentException("referenceId is required.", nameof(referenceId));

        // Validate form type
        var validFormTypes = new[] { "W-9", "W-8BEN", "W-8BEN-E" };
        if (!validFormTypes.Contains(formType))
            throw new ArgumentException($"formType must be one of: {string.Join(", ", validFormTypes)}", nameof(formType));

        // Clamp TTL to valid range
        ttl = Math.Clamp(ttl, 1, 86400);

        var response = new AvalaraFormRequestResponse();

        try
        {
            // Get the access token first
            var authResponse = await GetAccessTokenAsync(cancellationToken);
            if (!authResponse.Success || string.IsNullOrWhiteSpace(authResponse.AccessToken))
            {
                response.Success = false;
                response.ErrorMessage = $"Failed to obtain access token: {authResponse.ErrorMessage}";
                return response;
            }

            // Get configuration values
            var useSandbox = _configuration.GetValue<bool>("Avalara:UseSandbox", true);
            var apiBaseUrl = useSandbox
                ? _configuration["Avalara:SandboxApiUrl"] ?? "https://sbx.track1099.com"
                : _configuration["Avalara:ProductionApiUrl"] ?? "https://www.track1099.com";
            var teamApiId = _configuration["Avalara:TeamApiId"];
            var companyId = _configuration["Avalara:CompanyId"];

            if (string.IsNullOrWhiteSpace(teamApiId))
            {
                response.Success = false;
                response.ErrorMessage = "Avalara:TeamApiId configuration is missing.";
                return response;
            }

            if (string.IsNullOrWhiteSpace(companyId))
            {
                response.Success = false;
                response.ErrorMessage = "Avalara:CompanyId configuration is missing.";
                return response;
            }

            _logger.LogInformation("Creating Avalara form request: FormType={FormType}, ReferenceId={ReferenceId}", formType, referenceId);

            // Build the request URL
            var url = $"{apiBaseUrl.TrimEnd('/')}/api/v1/{teamApiId}/form_requests";

            // Build the request body according to JSON:API specification
            var requestBody = new
            {
                data = new
                {
                    type = "form_request",
                    attributes = new
                    {
                        form_type = formType,
                        company_id = int.Parse(companyId),
                        reference_id = referenceId,
                        ttl = ttl
                    }
                }
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/vnd.api+json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResponse.AccessToken);

            using var httpResponse = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            response.RawResponse = body;

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorMsg = $"Avalara form request failed: {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}";
                _logger.LogError("{Error}. Body: {Body}", errorMsg, body);
                response.Success = false;
                response.ErrorMessage = errorMsg;
                return response;
            }

            // Parse the response to extract key fields, but also return the full JSON
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("data", out var data))
            {
                response.FormRequestJson = body;
                
                if (data.TryGetProperty("id", out var id))
                    response.FormRequestId = id.GetString();

                if (data.TryGetProperty("attributes", out var attributes))
                {
                    if (attributes.TryGetProperty("form_type", out var ft))
                        response.FormType = ft.GetString();

                    if (attributes.TryGetProperty("expires_at", out var expiresAt))
                    {
                        if (DateTime.TryParse(expiresAt.GetString(), out var expiry))
                            response.ExpiresAt = expiry;
                    }
                }
            }

            response.Success = true;
            _logger.LogInformation("Avalara form request created successfully: FormRequestId={FormRequestId}", response.FormRequestId);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Avalara form request");
            response.Success = false;
            response.ErrorMessage = $"Exception during form request creation: {ex.Message}";
            return response;
        }
    }

    /// <summary>
    /// Internal class for deserializing OAuth token response.
    /// </summary>
    private sealed class AvalaraTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
