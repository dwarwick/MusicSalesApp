#nullable enable
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
/// API controller for managing tip payments from listeners to creators.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class TipController : ControllerBase
{
    private readonly ITipService _tipService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TipController> _logger;

    public TipController(
        ITipService tipService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<TipController> logger)
    {
        _tipService = tipService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Validates whether the current user can tip a creator.
    /// </summary>
    [HttpPost("validate")]
    public async Task<IActionResult> ValidateTip([FromBody] ValidateTipRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        var (canTip, errorMessage) = await _tipService.ValidateTipAsync(
            user.Id, request.CreatorId, request.Amount, ipAddress, request.Fingerprint);

        if (!canTip)
            return BadRequest(new { error = errorMessage });

        return Ok(new { canTip = true });
    }

    /// <summary>
    /// Creates a PayPal order for a tip payment.
    /// </summary>
    [HttpPost("create-order")]
    public async Task<IActionResult> CreateTipOrder([FromBody] CreateTipOrderRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        // Validate again server-side
        var (canTip, errorMessage) = await _tipService.ValidateTipAsync(
            user.Id, request.CreatorId, request.Amount, ipAddress, request.Fingerprint);

        if (!canTip)
            return BadRequest(new { error = errorMessage });

        try
        {
            var accessToken = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
                return StatusCode(500, new { error = "Failed to authenticate with PayPal." });

            var payPalBaseUrl = _configuration["PayPal:BaseUrl"] ?? "https://api-m.sandbox.paypal.com";
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var orderPayload = new
            {
                intent = "CAPTURE",
                purchase_units = new[]
                {
                    new
                    {
                        amount = new
                        {
                            currency_code = "USD",
                            value = request.Amount.ToString("F2")
                        },
                        description = $"Tip for creator on StreamTunes"
                    }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(orderPayload),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync($"{payPalBaseUrl}/v2/checkout/orders", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal create order failed: {Status} {Body}", response.StatusCode, responseBody);
                return StatusCode(500, new { error = "Failed to create PayPal order." });
            }

            using var doc = JsonDocument.Parse(responseBody);
            var orderId = doc.RootElement.GetProperty("id").GetString();

            return Ok(new { orderId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating PayPal tip order");
            return StatusCode(500, new { error = "An error occurred creating the payment." });
        }
    }

    /// <summary>
    /// Captures a PayPal tip payment and records the tip.
    /// </summary>
    [HttpPost("capture-order")]
    public async Task<IActionResult> CaptureTipOrder([FromBody] CaptureTipOrderRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        try
        {
            var accessToken = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
                return StatusCode(500, new { error = "Failed to authenticate with PayPal." });

            var payPalBaseUrl = _configuration["PayPal:BaseUrl"] ?? "https://api-m.sandbox.paypal.com";
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var captureResponse = await httpClient.PostAsync(
                $"{payPalBaseUrl}/v2/checkout/orders/{request.PayPalOrderId}/capture",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            var captureBody = await captureResponse.Content.ReadAsStringAsync();

            if (!captureResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal capture failed: {Status} {Body}", captureResponse.StatusCode, captureBody);
                return StatusCode(500, new { error = "Failed to capture payment." });
            }

            using var doc = JsonDocument.Parse(captureBody);
            var captureStatus = doc.RootElement.GetProperty("status").GetString();

            if (captureStatus != "COMPLETED")
            {
                return BadRequest(new { error = $"Payment not completed. Status: {captureStatus}" });
            }

            // Record the tip
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var tip = await _tipService.RecordTipAsync(
                user.Id,
                request.CreatorId,
                request.SongMetadataId,
                request.Amount,
                request.PayPalOrderId,
                ipAddress,
                request.Fingerprint);

            return Ok(new { success = true, tipId = tip.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error capturing PayPal tip order");
            return StatusCode(500, new { error = "An error occurred processing the payment." });
        }
    }

    /// <summary>
    /// Returns the PayPal client ID for tip payments.
    /// </summary>
    [HttpGet("paypal-client-id")]
    [AllowAnonymous]
    public IActionResult GetPayPalClientId()
    {
        var clientId = _configuration["PayPal:ClientId"];
        return Ok(new { clientId });
    }

    private async Task<string> GetPayPalAccessTokenAsync()
    {
        try
        {
            var clientId = _configuration["PayPal:ClientId"];
            var secret = _configuration["PayPal:Secret"];
            var payPalBaseUrl = _configuration["PayPal:BaseUrl"] ?? "https://api-m.sandbox.paypal.com";

            using var client = new HttpClient();
            client.BaseAddress = new Uri(payPalBaseUrl);

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
            _logger.LogError(ex, "Error getting PayPal access token for tips");
            return string.Empty;
        }
    }
}

public class ValidateTipRequest
{
    public int CreatorId { get; set; }
    public decimal Amount { get; set; }
    public string? Fingerprint { get; set; }
}

public class CreateTipOrderRequest
{
    public int CreatorId { get; set; }
    public decimal Amount { get; set; }
    public string? Fingerprint { get; set; }
}

public class CaptureTipOrderRequest
{
    public int CreatorId { get; set; }
    public int? SongMetadataId { get; set; }
    public decimal Amount { get; set; }
    public string PayPalOrderId { get; set; } = string.Empty;
    public string? Fingerprint { get; set; }
}
