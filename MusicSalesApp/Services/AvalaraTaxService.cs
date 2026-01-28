#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
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
