using System.Text;
using System.Text.Encodings.Web;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for processing stream payouts to creators.
/// </summary>
public class StreamPayoutService : IStreamPayoutService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StreamPayoutService> _logger;

    // Minimum payout threshold in USD
    private const decimal MinimumPayoutThreshold = 5.00m;
    
    // Minimum days between payouts (weekly = 7 days)
    private const int MinimumDaysBetweenPayouts = 7;

    public StreamPayoutService(
        IDbContextFactory<AppDbContext> contextFactory,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<StreamPayoutService> logger)
    {
        _contextFactory = contextFactory;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ProcessPendingPayoutsAsync()
    {
        _logger.LogInformation("Starting stream payout processing job");
        
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        var creatorsProcessed = 0;

        try
        {
            // Get all active creators with songs
            var creators = await context.Creators
                .Include(s => s.User)
                .Where(s => s.IsActive && s.OnboardingStatus == CreatorOnboardingStatus.Completed)
                .ToListAsync();

            foreach (var creator in creators)
            {
                try
                {
                    var payoutProcessed = await ProcessCreatorPayoutAsync(creator);
                    if (payoutProcessed)
                    {
                        creatorsProcessed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing payout for creator {CreatorId}", creator.Id);
                    // Continue with next creator
                }
            }

            _logger.LogInformation("Stream payout processing completed. Processed {Count} creators", creatorsProcessed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProcessPendingPayoutsAsync");
            throw;
        }

        return creatorsProcessed;
    }

    /// <summary>
    /// Processes payout for a single creator if they have reached the minimum threshold
    /// and haven't received a payout in the past week.
    /// </summary>
    private async Task<bool> ProcessCreatorPayoutAsync(Creator creator)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check if creator received a payout in the past 7 days
        var lastPayout = await context.StreamPayouts
            .Where(sp => sp.CreatorId == creator.Id)
            .OrderByDescending(sp => sp.PaymentDate)
            .FirstOrDefaultAsync();

        if (lastPayout != null)
        {
            var daysSinceLastPayout = (DateTime.UtcNow - lastPayout.PaymentDate).TotalDays;
            if (daysSinceLastPayout < MinimumDaysBetweenPayouts)
            {
                _logger.LogDebug("Creator {CreatorId} received a payout {Days:F1} days ago, skipping (weekly minimum not met)",
                    creator.Id, daysSinceLastPayout);
                return false;
            }
        }

        // Get all songs for this creator that have unpaid streams
        var creatorSongs = await context.SongMetadata
            .Where(sm => sm.CreatorId == creator.Id 
                      && sm.IsActive 
                      && !sm.IsAlbumCover
                      && sm.NumberOfStreams > sm.StreamsAtLastPayout)
            .ToListAsync();

        if (!creatorSongs.Any())
        {
            _logger.LogDebug("No unpaid streams for creator {CreatorId}", creator.Id);
            return false;
        }

        // Calculate total earnings for this payout
        var payoutRecords = new List<StreamPayout>();
        decimal totalAmount = 0;

        foreach (var song in creatorSongs)
        {
            var unpaidStreams = song.NumberOfStreams - song.StreamsAtLastPayout;
            var amountForSong = unpaidStreams * creator.StreamPayRate;

            if (amountForSong > 0)
            {
                payoutRecords.Add(new StreamPayout
                {
                    CreatorId = creator.Id,
                    SongMetadataId = song.Id,
                    NumberOfStreams = unpaidStreams,
                    RatePerStream = creator.StreamPayRate,
                    AmountPaid = amountForSong
                });

                totalAmount += amountForSong;
            }
        }

        // Check if total meets minimum threshold
        if (totalAmount < MinimumPayoutThreshold)
        {
            _logger.LogDebug("Creator {CreatorId} has ${Amount:F2} in unpaid streams, below ${Threshold:F2} threshold",
                creator.Id, totalAmount, MinimumPayoutThreshold);
            return false;
        }

        // Detailed logging for development/sandbox mode - Calculated data before PayPal call
        var sandboxMode = _configuration.GetValue<bool>("PayPal:SandboxMode", true);
        if (sandboxMode)
        {
            _logger.LogInformation("=== Stream Payout Calculation Summary (Development Mode) ===");
            _logger.LogInformation("Creator ID: {CreatorId}", creator.Id);
            _logger.LogInformation("PayPal Merchant ID: {MerchantId}", creator.PayPalMerchantId ?? "NOT SET");
            _logger.LogInformation("Number of Songs with Unpaid Streams: {SongCount}", payoutRecords.Count);
            _logger.LogInformation("Total Unpaid Streams: {TotalStreams:N0}", payoutRecords.Sum(p => p.NumberOfStreams));
            _logger.LogInformation("Stream Pay Rate: ${Rate:F6} per stream", creator.StreamPayRate);
            _logger.LogInformation("Total Calculated Amount: ${Amount:F2} USD", totalAmount);
            
            _logger.LogInformation("--- Per-Song Breakdown ---");
            foreach (var record in payoutRecords.OrderByDescending(p => p.AmountPaid))
            {
                var songTitle = creatorSongs.FirstOrDefault(s => s.Id == record.SongMetadataId)?.SongTitle ?? "Unknown";
                _logger.LogInformation("  Song: {Title} | Streams: {Streams:N0} | Amount: ${Amount:F2}",
                    songTitle, record.NumberOfStreams, record.AmountPaid);
            }
            _logger.LogInformation("=== END Calculation Summary ===");
        }

        // Process PayPal payout
        var payPalTransactionId = await ProcessPayPalPayoutAsync(creator, totalAmount);

        if (string.IsNullOrEmpty(payPalTransactionId))
        {
            _logger.LogError("Failed to process PayPal payout for creator {CreatorId}", creator.Id);
            return false;
        }

        // Save payout records and update StreamsAtLastPayout
        foreach (var payoutRecord in payoutRecords)
        {
            payoutRecord.PayPalTransactionId = payPalTransactionId;
            payoutRecord.PaymentDate = DateTime.UtcNow;
            context.StreamPayouts.Add(payoutRecord);

            // Update the song's StreamsAtLastPayout
            var song = creatorSongs.First(s => s.Id == payoutRecord.SongMetadataId);
            song.StreamsAtLastPayout = song.NumberOfStreams;
        }

        await context.SaveChangesAsync();

        // Send receipt email
        await SendPayoutReceiptEmailAsync(creator.Id, payoutRecords, totalAmount, payPalTransactionId);

        _logger.LogInformation("Processed payout for creator {CreatorId}: ${Amount:F2} for {Songs} songs",
            creator.Id, totalAmount, payoutRecords.Count);

        return true;
    }

    /// <summary>
    /// Processes a PayPal payout to the creator using PayPal Payouts API.
    /// </summary>
    private async Task<string> ProcessPayPalPayoutAsync(Creator creator, decimal amount)
    {
        var sandboxMode = _configuration.GetValue<bool>("PayPal:SandboxMode", true);
        
        // Detailed logging for development/sandbox mode
        if (sandboxMode)
        {
            _logger.LogInformation("=== PayPal Payout Request (Development Mode) ===");
            _logger.LogInformation("Creator ID: {CreatorId}", creator.Id);
            _logger.LogInformation("PayPal Merchant ID: {MerchantId}", creator.PayPalMerchantId ?? "NOT SET");
            _logger.LogInformation("Payout Amount: ${Amount:F2} USD", amount);
            _logger.LogInformation("PayPal Email (for payout): {PayPalEmail}", creator.PayPalEmail ?? "NOT SET");
            _logger.LogInformation("User Login Email: {LoginEmail}", creator.User?.Email ?? "NOT AVAILABLE");
            _logger.LogInformation("Request Time: {Time:yyyy-MM-dd HH:mm:ss} UTC", DateTime.UtcNow);
            _logger.LogInformation("=== END Request Data ===");
        }

        try
        {
            // Validate we have a PayPal email for the creator
            if (string.IsNullOrWhiteSpace(creator.PayPalEmail))
            {
                _logger.LogError("Creator {CreatorId} does not have a PayPal email configured", creator.Id);
                return string.Empty;
            }

            // Get PayPal access token
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Failed to get PayPal access token for payout");
                return string.Empty;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var bnCode = _configuration["PayPal:BNCode"];

            // Create PayPal Payouts API request
            var batchId = $"STREAM-PAYOUT-{DateTime.UtcNow:yyyyMMddHHmmss}-{creator.Id}";
            var payoutRequest = new
            {
                sender_batch_header = new
                {
                    sender_batch_id = batchId,
                    email_subject = "You have a payout from StreamTunes!",
                    email_message = $"You have received a payout of ${amount:F2} for streams of your music on StreamTunes."
                },
                items = new[]
                {
                    new
                    {
                        recipient_type = "EMAIL",
                        amount = new
                        {
                            value = amount.ToString("F2"),
                            currency = "USD"
                        },
                        receiver = creator.PayPalEmail,
                        note = $"StreamTunes stream payout for creator {creator.Id}",
                        sender_item_id = $"SELLER-{creator.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}"
                    }
                }
            };

            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(baseUrl);
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            httpClient.DefaultRequestHeaders.Add("Prefer", "return=representation");
            
            if (!string.IsNullOrWhiteSpace(bnCode))
            {
                httpClient.DefaultRequestHeaders.Add("PayPal-Partner-Attribution-Id", bnCode);
            }

            var jsonContent = System.Text.Json.JsonSerializer.Serialize(payoutRequest);
            var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("v1/payments/payouts", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (sandboxMode)
            {
                _logger.LogInformation("PayPal Payouts API Response Status: {Status}", response.StatusCode);
                _logger.LogInformation("PayPal Payouts API Response Body: {Body}", responseBody);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("PayPal payout failed for creator {CreatorId}: {Status} {Body}", 
                    creator.Id, response.StatusCode, responseBody);
                return string.Empty;
            }

            // Parse the payout batch ID from response
            using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
            var payoutBatchId = doc.RootElement.GetProperty("batch_header").GetProperty("payout_batch_id").GetString();

            // Detailed logging for development/sandbox mode - Response
            if (sandboxMode)
            {
                _logger.LogInformation("=== PayPal Payout Response (Development Mode) ===");
                _logger.LogInformation("Creator ID: {CreatorId}", creator.Id);
                _logger.LogInformation("PayPal Merchant ID: {MerchantId}", creator.PayPalMerchantId ?? "NOT SET");
                _logger.LogInformation("Transaction ID (payout_batch_id): {TransactionId}", payoutBatchId);
                _logger.LogInformation("Amount Paid: ${Amount:F2} USD", amount);
                _logger.LogInformation("Status: SUCCESS");
                _logger.LogInformation("Response Time: {Time:yyyy-MM-dd HH:mm:ss} UTC", DateTime.UtcNow);
                _logger.LogInformation("=== END Response Data ===");
            }

            _logger.LogInformation("PayPal payout successful for creator {CreatorId}: {PayoutBatchId}", creator.Id, payoutBatchId);
            
            return payoutBatchId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing PayPal payout for creator {CreatorId}", creator.Id);
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets a PayPal OAuth access token for API calls.
    /// </summary>
    private async Task<string> GetPayPalAccessTokenAsync()
    {
        var clientId = _configuration["PayPal:ClientId"];
        var secret = _configuration["PayPal:Secret"];
        var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
        
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret) || 
            clientId.Contains("REPLACE", StringComparison.OrdinalIgnoreCase) || 
            secret.Contains("REPLACE", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("PayPal ClientId/Secret not configured");
            return string.Empty;
        }

        try
        {
            using var client = new HttpClient();
            client.BaseAddress = new Uri(baseUrl);

            var authBytes = System.Text.Encoding.ASCII.GetBytes($"{clientId}:{secret}");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });
            var response = await client.PostAsync("v1/oauth2/token", content);
            var body = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal token request failed: {Status} {Body}", response.StatusCode, body);
                return string.Empty;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting PayPal access token");
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendPayoutReceiptEmailAsync(
        int creatorId,
        List<StreamPayout> payoutRecords,
        decimal totalAmount,
        string payPalTransactionId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var creator = await context.Creators
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.Id == creatorId);

            if (creator?.User == null)
            {
                _logger.LogError("Creator {CreatorId} or User not found for receipt email", creatorId);
                return false;
            }

            // Load song metadata for each payout record
            var songIds = payoutRecords.Select(p => p.SongMetadataId).ToList();
            var songs = await context.SongMetadata
                .Where(sm => songIds.Contains(sm.Id))
                .ToDictionaryAsync(sm => sm.Id);

            var baseUrl = _configuration["BaseUrl"] ?? "https://localhost:5001";
            var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";

            var body = BuildPayoutReceiptEmail(
                creator,
                payoutRecords,
                songs,
                totalAmount,
                payPalTransactionId,
                logoUrl,
                baseUrl);

            var subject = $"StreamTunes - Stream Payout Receipt (${totalAmount:F2})";
            
            return await _emailService.SendEmailAsync(creator.User.Email, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending payout receipt email to creator {CreatorId}", creatorId);
            return false;
        }
    }

    /// <summary>
    /// Builds the HTML email body for a payout receipt.
    /// </summary>
    private string BuildPayoutReceiptEmail(
        Creator creator,
        List<StreamPayout> payoutRecords,
        Dictionary<int, SongMetadata> songs,
        decimal totalAmount,
        string payPalTransactionId,
        string logoUrl,
        string baseUrl)
    {
        var body = new StringBuilder();

        // HTML encode user-provided data for security
        var encodedUserName = HtmlEncoder.Default.Encode(creator.User.UserName ?? "");
        var encodedTransactionId = HtmlEncoder.Default.Encode(payPalTransactionId);
        var encodedBaseUrl = HtmlEncoder.Default.Encode(baseUrl);
        var encodedLogoUrl = HtmlEncoder.Default.Encode(logoUrl);

        // Email header with logo
        body.Append($@"
        <div style='text-align: center; margin-bottom: 20px;'>
            <img src='{encodedLogoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
        </div>
        <h2>Stream Payout Receipt</h2>
        <p>Hi {encodedUserName},</p>
        <p>You've received a payout for streams of your music on StreamTunes!</p>
        ");

        // Payout summary
        body.Append($@"
        <div style='background-color: #f5f5f5; padding: 15px; border-radius: 5px; margin: 20px 0;'>
            <h3 style='margin-top: 0;'>Payout Summary</h3>
            <p><strong>Payment Date:</strong> {DateTime.UtcNow:MMMM dd, yyyy}</p>
            <p><strong>PayPal Transaction ID:</strong> {encodedTransactionId}</p>
            <p><strong>Total Amount:</strong> <span style='font-size: 20px; color: #28a745;'>${totalAmount:F2}</span></p>
        </div>
        ");

        // Itemized song details table
        body.Append(@"
        <h3>Itemized Details</h3>
        <table style='width: 100%; border-collapse: collapse; margin: 20px 0;'>
            <thead>
                <tr style='background-color: #e9ecef;'>
                    <th style='padding: 10px; text-align: left; border: 1px solid #dee2e6;'>Song Title</th>
                    <th style='padding: 10px; text-align: center; border: 1px solid #dee2e6;'>Streams</th>
                    <th style='padding: 10px; text-align: center; border: 1px solid #dee2e6;'>Rate per Stream</th>
                    <th style='padding: 10px; text-align: right; border: 1px solid #dee2e6;'>Amount</th>
                </tr>
            </thead>
            <tbody>
        ");

        foreach (var payout in payoutRecords.OrderByDescending(p => p.AmountPaid))
        {
            if (songs.TryGetValue(payout.SongMetadataId, out var song))
            {
                var songTitle = song.SongTitle ?? Path.GetFileNameWithoutExtension(song.Mp3BlobPath ?? "Unknown");
                var encodedSongTitle = HtmlEncoder.Default.Encode(songTitle);
                
                body.Append($@"
                <tr>
                    <td style='padding: 10px; border: 1px solid #dee2e6;'>{encodedSongTitle}</td>
                    <td style='padding: 10px; text-align: center; border: 1px solid #dee2e6;'>{payout.NumberOfStreams:N0}</td>
                    <td style='padding: 10px; text-align: center; border: 1px solid #dee2e6;'>${payout.RatePerStream:F6}</td>
                    <td style='padding: 10px; text-align: right; border: 1px solid #dee2e6;'>${payout.AmountPaid:F2}</td>
                </tr>
                ");
            }
        }

        body.Append($@"
            </tbody>
            <tfoot>
                <tr style='background-color: #f8f9fa; font-weight: bold;'>
                    <td colspan='3' style='padding: 10px; text-align: right; border: 1px solid #dee2e6;'>Total:</td>
                    <td style='padding: 10px; text-align: right; border: 1px solid #dee2e6;'>${totalAmount:F2}</td>
                </tr>
            </tfoot>
        </table>
        ");

        // Footer
        body.Append($@"
        <p style='margin-top: 30px;'>The payment has been sent to your PayPal account associated with your creator account.</p>
        <p>Thank you for sharing your music on StreamTunes!</p>
        <p style='color: #999; font-size: 12px; margin-top: 30px;'>
            <a href='{encodedBaseUrl}/manage-account' style='color: #666; text-decoration: underline;'>Manage your account preferences</a>
        </p>
        ");

        return body.ToString();
    }

    /// <inheritdoc />
    public async Task<decimal> GetUnpaidEarningsAsync(int creatorId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            return 0;
        }

        var creatorSongs = await context.SongMetadata
            .Where(sm => sm.CreatorId == creatorId 
                      && sm.IsActive 
                      && !sm.IsAlbumCover
                      && sm.NumberOfStreams > sm.StreamsAtLastPayout)
            .ToListAsync();

        decimal totalUnpaid = 0;
        foreach (var song in creatorSongs)
        {
            var unpaidStreams = song.NumberOfStreams - song.StreamsAtLastPayout;
            totalUnpaid += unpaidStreams * creator.StreamPayRate;
        }

        return totalUnpaid;
    }

    /// <inheritdoc />
    public async Task<List<StreamPayout>> GetPayoutHistoryAsync(int creatorId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.StreamPayouts
            .Include(sp => sp.SongMetadata)
            .Where(sp => sp.CreatorId == creatorId)
            .OrderByDescending(sp => sp.PaymentDate)
            .ToListAsync();
    }
}
