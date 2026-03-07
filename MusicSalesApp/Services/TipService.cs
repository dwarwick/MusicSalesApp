#nullable enable
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing tips from listeners to creators.
/// </summary>
public class TipService : ITipService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TipService> _logger;
    private readonly IEmailService _emailService;

    private const decimal MinTipAmount = 1.00m;
    private const decimal MaxTipAmount = 50.00m;
    private const int MaxTipsPerHour = 5;
    private const int MaxTipsToSameCreator = 10;
    private const int MinAccountAgeDays = 7;
    private const int HoldPeriodDays = 7;

    public TipService(
        IDbContextFactory<AppDbContext> contextFactory,
        IConfiguration configuration,
        ILogger<TipService> logger,
        IEmailService emailService)
    {
        _contextFactory = contextFactory;
        _configuration = configuration;
        _logger = logger;
        _emailService = emailService;
    }

    /// <inheritdoc />
    public async Task<(bool CanTip, string? ErrorMessage)> ValidateTipAsync(
        int tipperUserId, int creatorId, decimal amount, string? ipAddress, string? fingerprint)
    {
        // Validate amount
        if (amount < MinTipAmount)
            return (false, $"Minimum tip amount is ${MinTipAmount:F2}.");
        if (amount > MaxTipAmount)
            return (false, $"Maximum tip amount is ${MaxTipAmount:F2}.");

        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check account age (at least 7 days old)
        var user = await context.Users.FindAsync(tipperUserId);
        if (user == null)
            return (false, "User not found.");

        var accountCreated = await context.UserHistories
            .Where(uh => uh.UserId == tipperUserId && uh.EventType == "AccountCreated")
            .Select(uh => uh.OccurredAt)
            .FirstOrDefaultAsync();

        // If no AccountCreated history, fall back (old accounts won't have history but should be fine)
        if (accountCreated != default && (DateTime.UtcNow - accountCreated).TotalDays < MinAccountAgeDays)
            return (false, $"Your account must be at least {MinAccountAgeDays} days old to send tips.");

        // Check self-tipping
        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
            return (false, "Creator not found.");
        if (creator.UserId == tipperUserId)
            return (false, "You cannot tip yourself.");

        // Rate limit: max 5 tips per hour per user
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentTipCount = await context.Tips
            .CountAsync(t => t.TipperUserId == tipperUserId && t.CreatedAt >= oneHourAgo);
        if (recentTipCount >= MaxTipsPerHour)
            return (false, $"You can send a maximum of {MaxTipsPerHour} tips per hour. Please try again later.");

        // Rate limit: max 10 tips from a user to the same creator (lifetime)
        var tipsToCreator = await context.Tips
            .CountAsync(t => t.TipperUserId == tipperUserId && t.CreatorId == creatorId);
        if (tipsToCreator >= MaxTipsToSameCreator)
            return (false, "You have reached the maximum number of tips to this creator.");

        // Fraud detection: check IP and fingerprint patterns
        if (!string.IsNullOrEmpty(ipAddress) || !string.IsNullOrEmpty(fingerprint))
        {
            var suspiciousTips = await context.Tips
                .CountAsync(t => t.CreatedAt >= oneHourAgo &&
                    t.TipperUserId != tipperUserId &&
                    ((ipAddress != null && t.IpAddress == ipAddress) ||
                     (fingerprint != null && t.MachineFingerprint == fingerprint)));

            if (suspiciousTips >= MaxTipsPerHour)
            {
                _logger.LogWarning(
                    "Suspicious tipping activity detected. IP: {IP}, Fingerprint: {FP}, UserId: {UserId}",
                    ipAddress, fingerprint, tipperUserId);
                return (false, "Unusual activity detected. Please try again later.");
            }
        }

        return (true, null);
    }

    /// <inheritdoc />
    public async Task<(bool Success, string? ErrorMessage, string? ApprovalUrl)> CreateTipOrderAsync(
        int tipperUserId, int creatorId, int? songMetadataId,
        decimal amount, string? ipAddress, string? fingerprint, string returnUrl)
    {
        // Validate first
        var (canTip, validationError) = await ValidateTipAsync(tipperUserId, creatorId, amount, ipAddress, fingerprint);
        if (!canTip)
            return (false, validationError, null);

        try
        {
            // Create PayPal order with return/cancel URLs
            var (orderId, approvalUrl, createError) = await CreatePayPalOrderAsync(amount, returnUrl);
            if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(approvalUrl))
                return (false, createError ?? "Failed to create payment.", null);

            // Record the tip in pending state (payment not yet captured)
            await using var context = await _contextFactory.CreateDbContextAsync();

            var tip = new Tip
            {
                TipperUserId = tipperUserId,
                CreatorId = creatorId,
                SongMetadataId = songMetadataId,
                Amount = amount,
                Status = TipStatus.Pending,
                PayPalOrderId = orderId,
                IpAddress = ipAddress,
                MachineFingerprint = fingerprint,
                CreatedAt = DateTime.UtcNow
            };

            context.Tips.Add(tip);
            await context.SaveChangesAsync();

            _logger.LogInformation(
                "Tip order created: ${Amount} from user {TipperId} to creator {CreatorId}, PayPal order {OrderId}",
                amount, tipperUserId, creatorId, orderId);

            return (true, null, approvalUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating tip order from user {UserId} to creator {CreatorId}", tipperUserId, creatorId);
            return (false, "An error occurred creating the payment.", null);
        }
    }

    /// <inheritdoc />
    public async Task<(bool Success, string? ErrorMessage, decimal Amount)> CaptureTipAsync(string payPalOrderId)
    {
        try
        {
            // Find the tip record
            await using var context = await _contextFactory.CreateDbContextAsync();
            var tip = await context.Tips
                .FirstOrDefaultAsync(t => t.PayPalOrderId == payPalOrderId && t.Status == TipStatus.Pending);

            if (tip == null)
            {
                _logger.LogWarning("No pending tip found for PayPal order {OrderId}", payPalOrderId);
                return (false, "Tip not found or already processed.", 0);
            }

            // Capture the PayPal order
            var (captured, captureError) = await CapturePayPalOrderAsync(payPalOrderId);
            if (!captured)
            {
                // Remove the uncaptured tip record
                context.Tips.Remove(tip);
                await context.SaveChangesAsync();
                return (false, captureError ?? "Failed to capture payment.", 0);
            }

            await context.SaveChangesAsync();

            _logger.LogInformation(
                "Tip captured: ${Amount} from user {TipperId} to creator {CreatorId}, PayPal order {OrderId}",
                tip.Amount, tip.TipperUserId, tip.CreatorId, payPalOrderId);

            // Send receipt email to the tipper (fire and forget, don't block on email)
            _ = SendTipReceiptEmailAsync(tip.TipperUserId, tip.CreatorId, tip.Amount, tip.SongMetadataId);

            return (true, null, tip.Amount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error capturing tip for PayPal order {OrderId}", payPalOrderId);
            return (false, "An error occurred processing the payment.", 0);
        }
    }

    /// <inheritdoc />
    public async Task<List<Tip>> GetTipsForCreatorAsync(int creatorId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Tips
            .Include(t => t.TipperUser)
            .Include(t => t.SongMetadata)
            .Where(t => t.CreatorId == creatorId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Tip>> GetClearedTipsForPayoutAsync(int creatorId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Tips
            .Where(t => t.CreatorId == creatorId && t.Status == TipStatus.Cleared)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> ProcessPendingToClearedAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Tips that have been pending for at least 7 full days
        var cutoff = DateTime.UtcNow.AddDays(-HoldPeriodDays);

        var pendingTips = await context.Tips
            .Where(t => t.Status == TipStatus.Pending && t.CreatedAt <= cutoff)
            .ToListAsync();

        foreach (var tip in pendingTips)
        {
            tip.Status = TipStatus.Cleared;
        }

        var count = await context.SaveChangesAsync();
        if (count > 0)
        {
            _logger.LogInformation("Cleared {Count} tips that passed the 7-day hold period", pendingTips.Count);
        }

        return pendingTips.Count;
    }

    /// <inheritdoc />
    public async Task MarkTipsAsPaidAsync(List<int> tipIds, string payPalPayoutTransactionId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var tips = await context.Tips
            .Where(t => tipIds.Contains(t.Id) && t.Status == TipStatus.Cleared)
            .ToListAsync();

        foreach (var tip in tips)
        {
            tip.Status = TipStatus.Paid;
            tip.PaidAt = DateTime.UtcNow;
            tip.PayPalPayoutTransactionId = payPalPayoutTransactionId;
        }

        await context.SaveChangesAsync();
        _logger.LogInformation("Marked {Count} tips as paid with transaction {TxId}", tips.Count, payPalPayoutTransactionId);
    }

    /// <inheritdoc />
    public async Task SendTipReceiptEmailAsync(int tipperUserId, int creatorId, decimal amount, int? songMetadataId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var tipper = await context.Users.FindAsync(tipperUserId);
            if (tipper?.Email == null) return;

            var creator = await context.Creators
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.Id == creatorId);
            if (creator == null) return;

            var artistName = creator.DisplayName ?? creator.User?.Email?.Split('@')[0] ?? "a creator";

            string? songTitle = null;
            if (songMetadataId.HasValue)
            {
                var song = await context.SongMetadata.FindAsync(songMetadataId.Value);
                if (song != null)
                    songTitle = song.SongTitle ?? Path.GetFileNameWithoutExtension(song.Mp3BlobPath ?? "Unknown");
            }

            var logoUrl = _emailService.GetLogoUrl();
            var baseUrl = _emailService.GetAppBaseUrl();
            var encodedArtist = HtmlEncoder.Default.Encode(artistName);
            var encodedLogo = HtmlEncoder.Default.Encode(logoUrl);
            var encodedBase = HtmlEncoder.Default.Encode(baseUrl);

            var body = new StringBuilder();
            body.Append($@"
            <div style='text-align: center; margin-bottom: 20px;'>
                <img src='{encodedLogo}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
            </div>
            <h2>Tip Receipt</h2>
            <p>Hi {HtmlEncoder.Default.Encode(tipper.UserName ?? tipper.Email)},</p>
            <p>Your tip has been successfully sent!</p>
            <div style='background-color: #f5f5f5; padding: 15px; border-radius: 5px; margin: 20px 0;'>
                <p><strong>Amount:</strong> <span style='font-size: 20px; color: #28a745;'>${amount:F2}</span></p>
                <p><strong>Artist:</strong> {encodedArtist}</p>");

            if (!string.IsNullOrEmpty(songTitle))
            {
                body.Append($@"
                <p><strong>Song:</strong> {HtmlEncoder.Default.Encode(songTitle)}</p>");
            }

            body.Append($@"
                <p><strong>Date:</strong> {DateTime.UtcNow:MMMM dd, yyyy 'at' HH:mm} UTC</p>
            </div>
            <p>Tips are held for 7 days before being paid out to the creator. Thank you for supporting artists on StreamTunes!</p>
            <p style='color: #999; font-size: 12px; margin-top: 30px;'>
                <a href='{encodedBase}/manage-account' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
            </p>");

            var subject = $"StreamTunes - Tip Receipt (${amount:F2} to {artistName})";
            await _emailService.SendEmailAsync(tipper.Email, subject, body.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send tip receipt email to user {UserId}", tipperUserId);
        }
    }

    /// <inheritdoc />
    public async Task<List<Tip>> GetAllTipsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.Tips
            .Include(t => t.TipperUser)
            .Include(t => t.Creator)
                .ThenInclude(c => c.User)
            .Include(t => t.SongMetadata)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    private async Task<(string? OrderId, string? ApprovalUrl, string? Error)> CreatePayPalOrderAsync(decimal amount, string returnUrl)
    {
        try
        {
            var accessToken = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
                return (null, null, "Failed to authenticate with PayPal.");

            var payPalBaseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Build return and cancel URLs
            var separator = returnUrl.Contains('?') ? "&" : "?";
            var tipReturnUrl = $"{returnUrl}{separator}tip_status=approved";
            var tipCancelUrl = $"{returnUrl}{separator}tip_status=cancelled";

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
                            value = amount.ToString("F2")
                        },
                        description = "Tip for creator on StreamTunes"
                    }
                },
                payment_source = new
                {
                    paypal = new
                    {
                        experience_context = new
                        {
                            payment_method_preference = "IMMEDIATE_PAYMENT_REQUIRED",
                            brand_name = "StreamTunes",
                            locale = "en-US",
                            landing_page = "LOGIN",
                            shipping_preference = "NO_SHIPPING",
                            user_action = "PAY_NOW",
                            return_url = tipReturnUrl,
                            cancel_url = tipCancelUrl
                        }
                    }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(orderPayload),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync($"{payPalBaseUrl}v2/checkout/orders", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal create order failed: {Status} {Body}", response.StatusCode, responseBody);
                return (null, null, "Failed to create PayPal order.");
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var orderId = root.GetProperty("id").GetString();

            // Extract the approval URL from HATEOAS links
            string? approvalUrl = null;
            if (root.TryGetProperty("links", out var links))
            {
                foreach (var link in links.EnumerateArray())
                {
                    if (link.TryGetProperty("rel", out var rel) && rel.GetString() == "payer-action")
                    {
                        approvalUrl = link.GetProperty("href").GetString();
                        break;
                    }
                }
            }

            return (orderId, approvalUrl, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating PayPal tip order");
            return (null, null, "An error occurred creating the payment.");
        }
    }

    private async Task<(bool Captured, string? Error)> CapturePayPalOrderAsync(string orderId)
    {
        try
        {
            var accessToken = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
                return (false, "Failed to authenticate with PayPal.");

            var payPalBaseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var captureResponse = await httpClient.PostAsync(
                $"{payPalBaseUrl}v2/checkout/orders/{orderId}/capture",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            var captureBody = await captureResponse.Content.ReadAsStringAsync();

            if (!captureResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal capture failed: {Status} {Body}", captureResponse.StatusCode, captureBody);
                return (false, "Failed to capture payment.");
            }

            using var doc = JsonDocument.Parse(captureBody);
            var captureStatus = doc.RootElement.GetProperty("status").GetString();

            if (captureStatus != "COMPLETED")
                return (false, $"Payment not completed. Status: {captureStatus}");

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error capturing PayPal tip order {OrderId}", orderId);
            return (false, "An error occurred processing the payment.");
        }
    }

    private async Task<string> GetPayPalAccessTokenAsync()
    {
        try
        {
            var clientId = _configuration["PayPal:ClientId"];
            var secret = _configuration["PayPal:Secret"];
            var payPalBaseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";

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
