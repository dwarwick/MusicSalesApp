using System.Text;
using System.Text.Encodings.Web;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for processing stream payouts to sellers.
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
        
        var sellersProcessed = 0;

        try
        {
            // Get all active sellers with songs
            var sellers = await context.Sellers
                .Include(s => s.User)
                .Where(s => s.IsActive && s.OnboardingStatus == SellerOnboardingStatus.Completed)
                .ToListAsync();

            foreach (var seller in sellers)
            {
                try
                {
                    var payoutProcessed = await ProcessSellerPayoutAsync(seller);
                    if (payoutProcessed)
                    {
                        sellersProcessed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing payout for seller {SellerId}", seller.Id);
                    // Continue with next seller
                }
            }

            _logger.LogInformation("Stream payout processing completed. Processed {Count} sellers", sellersProcessed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProcessPendingPayoutsAsync");
            throw;
        }

        return sellersProcessed;
    }

    /// <summary>
    /// Processes payout for a single seller if they have reached the minimum threshold
    /// and haven't received a payout in the past week.
    /// </summary>
    private async Task<bool> ProcessSellerPayoutAsync(Seller seller)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check if seller received a payout in the past 7 days
        var lastPayout = await context.StreamPayouts
            .Where(sp => sp.SellerId == seller.Id)
            .OrderByDescending(sp => sp.PaymentDate)
            .FirstOrDefaultAsync();

        if (lastPayout != null)
        {
            var daysSinceLastPayout = (DateTime.UtcNow - lastPayout.PaymentDate).TotalDays;
            if (daysSinceLastPayout < MinimumDaysBetweenPayouts)
            {
                _logger.LogDebug("Seller {SellerId} received a payout {Days:F1} days ago, skipping (weekly minimum not met)",
                    seller.Id, daysSinceLastPayout);
                return false;
            }
        }

        // Get all songs for this seller that have unpaid streams
        var sellerSongs = await context.SongMetadata
            .Where(sm => sm.SellerId == seller.Id 
                      && sm.IsActive 
                      && !sm.IsAlbumCover
                      && sm.NumberOfStreams > sm.StreamsAtLastPayout)
            .ToListAsync();

        if (!sellerSongs.Any())
        {
            _logger.LogDebug("No unpaid streams for seller {SellerId}", seller.Id);
            return false;
        }

        // Calculate total earnings for this payout
        var payoutRecords = new List<StreamPayout>();
        decimal totalAmount = 0;

        foreach (var song in sellerSongs)
        {
            var unpaidStreams = song.NumberOfStreams - song.StreamsAtLastPayout;
            var amountForSong = unpaidStreams * seller.StreamPayRate;

            if (amountForSong > 0)
            {
                payoutRecords.Add(new StreamPayout
                {
                    SellerId = seller.Id,
                    SongMetadataId = song.Id,
                    NumberOfStreams = unpaidStreams,
                    RatePerStream = seller.StreamPayRate,
                    AmountPaid = amountForSong
                });

                totalAmount += amountForSong;
            }
        }

        // Check if total meets minimum threshold
        if (totalAmount < MinimumPayoutThreshold)
        {
            _logger.LogDebug("Seller {SellerId} has ${Amount:F2} in unpaid streams, below ${Threshold:F2} threshold",
                seller.Id, totalAmount, MinimumPayoutThreshold);
            return false;
        }

        // Detailed logging for development/sandbox mode - Calculated data before PayPal call
        var sandboxMode = _configuration.GetValue<bool>("PayPal:SandboxMode", true);
        if (sandboxMode)
        {
            _logger.LogInformation("=== Stream Payout Calculation Summary (Development Mode) ===");
            _logger.LogInformation("Seller ID: {SellerId}", seller.Id);
            _logger.LogInformation("PayPal Merchant ID: {MerchantId}", seller.PayPalMerchantId ?? "NOT SET");
            _logger.LogInformation("Number of Songs with Unpaid Streams: {SongCount}", payoutRecords.Count);
            _logger.LogInformation("Total Unpaid Streams: {TotalStreams:N0}", payoutRecords.Sum(p => p.NumberOfStreams));
            _logger.LogInformation("Stream Pay Rate: ${Rate:F6} per stream", seller.StreamPayRate);
            _logger.LogInformation("Total Calculated Amount: ${Amount:F2} USD", totalAmount);
            
            _logger.LogInformation("--- Per-Song Breakdown ---");
            foreach (var record in payoutRecords.OrderByDescending(p => p.AmountPaid))
            {
                var songTitle = sellerSongs.FirstOrDefault(s => s.Id == record.SongMetadataId)?.SongTitle ?? "Unknown";
                _logger.LogInformation("  Song: {Title} | Streams: {Streams:N0} | Amount: ${Amount:F2}",
                    songTitle, record.NumberOfStreams, record.AmountPaid);
            }
            _logger.LogInformation("=== END Calculation Summary ===");
        }

        // Process PayPal payout
        var payPalTransactionId = await ProcessPayPalPayoutAsync(seller, totalAmount);

        if (string.IsNullOrEmpty(payPalTransactionId))
        {
            _logger.LogError("Failed to process PayPal payout for seller {SellerId}", seller.Id);
            return false;
        }

        // Save payout records and update StreamsAtLastPayout
        foreach (var payoutRecord in payoutRecords)
        {
            payoutRecord.PayPalTransactionId = payPalTransactionId;
            payoutRecord.PaymentDate = DateTime.UtcNow;
            context.StreamPayouts.Add(payoutRecord);

            // Update the song's StreamsAtLastPayout
            var song = sellerSongs.First(s => s.Id == payoutRecord.SongMetadataId);
            song.StreamsAtLastPayout = song.NumberOfStreams;
        }

        await context.SaveChangesAsync();

        // Send receipt email
        await SendPayoutReceiptEmailAsync(seller.Id, payoutRecords, totalAmount, payPalTransactionId);

        _logger.LogInformation("Processed payout for seller {SellerId}: ${Amount:F2} for {Songs} songs",
            seller.Id, totalAmount, payoutRecords.Count);

        return true;
    }

    /// <summary>
    /// Processes a PayPal payout to the seller.
    /// </summary>
    private async Task<string> ProcessPayPalPayoutAsync(Seller seller, decimal amount)
    {
        // TODO: Implement actual PayPal Payouts API integration
        // For now, return a mock transaction ID
        // In production, this would call PayPal's Payouts API
        
        var sandboxMode = _configuration.GetValue<bool>("PayPal:SandboxMode", true);
        
        // Detailed logging for development/sandbox mode
        if (sandboxMode)
        {
            _logger.LogInformation("=== PayPal Payout Request (Development Mode) ===");
            _logger.LogInformation("Seller ID: {SellerId}", seller.Id);
            _logger.LogInformation("PayPal Merchant ID: {MerchantId}", seller.PayPalMerchantId ?? "NOT SET");
            _logger.LogInformation("Payout Amount: ${Amount:F2} USD", amount);
            _logger.LogInformation("Seller Email: {Email}", seller.User?.Email ?? "NOT AVAILABLE");
            _logger.LogInformation("Request Time: {Time:yyyy-MM-dd HH:mm:ss} UTC", DateTime.UtcNow);
            _logger.LogInformation("=== END Request Data ===");
        }
        
        _logger.LogWarning("PayPal payout integration not yet implemented. Mock transaction ID generated for seller {SellerId}", seller.Id);
        
        // Generate a mock transaction ID for testing
        var mockTransactionId = $"PAYOUT-{DateTime.UtcNow:yyyyMMddHHmmss}-{seller.Id}";
        
        // Simulate async PayPal API call
        await Task.Delay(100);
        
        // Detailed logging for development/sandbox mode - Response
        if (sandboxMode)
        {
            _logger.LogInformation("=== PayPal Payout Response (Development Mode - MOCK) ===");
            _logger.LogInformation("Seller ID: {SellerId}", seller.Id);
            _logger.LogInformation("PayPal Merchant ID: {MerchantId}", seller.PayPalMerchantId ?? "NOT SET");
            _logger.LogInformation("Transaction ID: {TransactionId}", mockTransactionId);
            _logger.LogInformation("Amount Paid: ${Amount:F2} USD", amount);
            _logger.LogInformation("Status: SUCCESS (MOCK)");
            _logger.LogInformation("Response Time: {Time:yyyy-MM-dd HH:mm:ss} UTC", DateTime.UtcNow);
            _logger.LogInformation("=== END Response Data ===");
            _logger.LogWarning("NOTE: This is a MOCK payout. Real PayPal API not called yet.");
            _logger.LogWarning("Check PayPal Sandbox at: https://www.sandbox.paypal.com/");
        }
        
        return mockTransactionId;
    }

    /// <inheritdoc />
    public async Task<bool> SendPayoutReceiptEmailAsync(
        int sellerId,
        List<StreamPayout> payoutRecords,
        decimal totalAmount,
        string payPalTransactionId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var seller = await context.Sellers
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.Id == sellerId);

            if (seller?.User == null)
            {
                _logger.LogError("Seller {SellerId} or User not found for receipt email", sellerId);
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
                seller,
                payoutRecords,
                songs,
                totalAmount,
                payPalTransactionId,
                logoUrl,
                baseUrl);

            var subject = $"StreamTunes - Stream Payout Receipt (${totalAmount:F2})";
            
            return await _emailService.SendEmailAsync(seller.User.Email, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending payout receipt email to seller {SellerId}", sellerId);
            return false;
        }
    }

    /// <summary>
    /// Builds the HTML email body for a payout receipt.
    /// </summary>
    private string BuildPayoutReceiptEmail(
        Seller seller,
        List<StreamPayout> payoutRecords,
        Dictionary<int, SongMetadata> songs,
        decimal totalAmount,
        string payPalTransactionId,
        string logoUrl,
        string baseUrl)
    {
        var body = new StringBuilder();

        // HTML encode user-provided data for security
        var encodedUserName = HtmlEncoder.Default.Encode(seller.User.UserName ?? "");
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
        <p style='margin-top: 30px;'>The payment has been sent to your PayPal account associated with your seller account.</p>
        <p>Thank you for sharing your music on StreamTunes!</p>
        <p style='color: #999; font-size: 12px; margin-top: 30px;'>
            <a href='{encodedBaseUrl}/manage-account' style='color: #666; text-decoration: underline;'>Manage your account preferences</a>
        </p>
        ");

        return body.ToString();
    }

    /// <inheritdoc />
    public async Task<decimal> GetUnpaidEarningsAsync(int sellerId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var seller = await context.Sellers.FindAsync(sellerId);
        if (seller == null)
        {
            return 0;
        }

        var sellerSongs = await context.SongMetadata
            .Where(sm => sm.SellerId == sellerId 
                      && sm.IsActive 
                      && !sm.IsAlbumCover
                      && sm.NumberOfStreams > sm.StreamsAtLastPayout)
            .ToListAsync();

        decimal totalUnpaid = 0;
        foreach (var song in sellerSongs)
        {
            var unpaidStreams = song.NumberOfStreams - song.StreamsAtLastPayout;
            totalUnpaid += unpaidStreams * seller.StreamPayRate;
        }

        return totalUnpaid;
    }

    /// <inheritdoc />
    public async Task<List<StreamPayout>> GetPayoutHistoryAsync(int sellerId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.StreamPayouts
            .Include(sp => sp.SongMetadata)
            .Where(sp => sp.SellerId == sellerId)
            .OrderByDescending(sp => sp.PaymentDate)
            .ToListAsync();
    }
}
