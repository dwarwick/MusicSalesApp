#nullable enable
using System.Text;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for sending purchase confirmation emails.
/// </summary>
public class PurchaseEmailService : IPurchaseEmailService
{
    private readonly IEmailService _emailService;
    private readonly IAzureStorageService _azureStorageService;
    private readonly ILogger<PurchaseEmailService> _logger;
    private readonly IConfiguration _configuration;

    public PurchaseEmailService(
        IEmailService emailService,
        IAzureStorageService azureStorageService,
        ILogger<PurchaseEmailService> logger,
        IConfiguration configuration)
    {
        _emailService = emailService;
        _azureStorageService = azureStorageService;
        _logger = logger;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public async Task<bool> SendSongPurchaseConfirmationAsync(
        string userEmail,
        string userName,
        string streamTunesOrderId,
        string payPalOrderId,
        IEnumerable<CartItemWithMetadata> purchasedItems,
        decimal totalAmount,
        string? timeZoneId,
        string baseUrl)
    {
        try
        {
            var itemsList = purchasedItems.ToList();
            var logoUrl = _emailService.GetLogoUrl();

            // Group items by album
            var albumGroups = itemsList
                .Where(i => i.IsAlbumTrack)
                .GroupBy(i => i.AlbumName!)
                .ToList();

            var standaloneSongs = itemsList
                .Where(i => !i.IsAlbumTrack)
                .ToList();

            var body = new StringBuilder();
            body.Append(BuildEmailHeader(logoUrl, "Purchase Confirmation"));
            body.Append(BuildGreeting(userName));
            body.Append("<p style='font-size: 16px; color: #333;'>Thank you for your purchase! Here's a summary of your order:</p>");

            // Order information
            body.Append(BuildOrderInfoSection(streamTunesOrderId, payPalOrderId, timeZoneId));

            // Standalone songs section
            if (standaloneSongs.Any())
            {
                body.Append(BuildStandaloneSongsSection(standaloneSongs, baseUrl));
            }

            // Album sections
            foreach (var albumGroup in albumGroups)
            {
                body.Append(BuildAlbumSection(albumGroup.Key, albumGroup.ToList(), baseUrl));
            }

            // Total section
            body.Append(BuildTotalSection(totalAmount));
            body.Append(BuildEmailFooter());

            var subject = "StreamTunes - Purchase Confirmation";
            return await _emailService.SendEmailAsync(userEmail, subject, body.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending purchase confirmation email to {Email}", userEmail);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendSubscriptionConfirmationAsync(
        string userEmail,
        string userName,
        Subscription subscription,
        string externalSubscriptionId,
        string? timeZoneId,
        string baseUrl)
    {
        try
        {
            var logoUrl = _emailService.GetLogoUrl();

            var body = new StringBuilder();
            body.Append(BuildEmailHeader(logoUrl, "Subscription Confirmation"));
            body.Append(BuildGreeting(userName));
            body.Append("<p style='font-size: 16px; color: #333;'>Thank you for subscribing to StreamTunes! Here are your subscription details:</p>");

            // Subscription details section
            body.Append(BuildSubscriptionDetailsSection(subscription, externalSubscriptionId, timeZoneId));

            body.Append(BuildSubscriptionBenefitsSection());

            // Terms and cancellation info
            body.Append(BuildSubscriptionTermsSection(subscription, timeZoneId));

            body.Append(BuildEmailFooter());

            var subject = "StreamTunes - Subscription Confirmation";
            return await _emailService.SendEmailAsync(userEmail, subject, body.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending subscription confirmation email to {Email}", userEmail);
            return false;
        }
    }

    private string BuildEmailHeader(string logoUrl, string title)
    {
        return $@"
        <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
            <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>{title}</h1>
            </div>
            <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
        ";
    }

    private string BuildGreeting(string userName)
    {
        var displayName = string.IsNullOrEmpty(userName) ? "Valued Customer" : userName;
        return $"<p style='font-size: 16px; color: #333;'>Hello {System.Web.HttpUtility.HtmlEncode(displayName)},</p>";
    }

    private string BuildOrderInfoSection(string streamTunesOrderId, string payPalOrderId, string? timeZoneId)
    {
        var purchaseDateDisplay = UserTimeZoneDisplayHelper.FormatDateTimeWithTimeZone(DateTime.UtcNow, timeZoneId);

        return $@"
            <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                <h3 style='margin: 0 0 10px 0; color: #333; font-size: 18px;'>Order Information</h3>
                <table style='width: 100%; border-collapse: collapse;'>
                    <tr>
                        <td style='padding: 5px 0; color: #666;'>StreamTunes Order ID:</td>
                        <td style='padding: 5px 0; color: #333; font-weight: bold;'>{System.Web.HttpUtility.HtmlEncode(streamTunesOrderId)}</td>
                    </tr>
                    <tr>
                        <td style='padding: 5px 0; color: #666;'>PayPal Order ID:</td>
                        <td style='padding: 5px 0; color: #333; font-weight: bold;'>{System.Web.HttpUtility.HtmlEncode(payPalOrderId)}</td>
                    </tr>
                    <tr>
                        <td style='padding: 5px 0; color: #666;'>Purchase Date:</td>
                        <td style='padding: 5px 0; color: #333;'>{purchaseDateDisplay}</td>
                    </tr>
                </table>
            </div>
        ";
    }

    private string BuildStandaloneSongsSection(List<CartItemWithMetadata> songs, string baseUrl)
    {
        var sb = new StringBuilder();
        sb.Append(@"
            <div style='margin: 20px 0;'>
                <h3 style='color: #333; font-size: 18px; border-bottom: 2px solid #1a1a2e; padding-bottom: 10px;'>Individual Songs</h3>
                <table style='width: 100%; border-collapse: collapse;'>
                    <thead>
                        <tr style='background-color: #f5f5f5;'>
                            <th style='padding: 10px; text-align: left; border-bottom: 1px solid #ddd;'>Song</th>
                            <th style='padding: 10px; text-align: right; border-bottom: 1px solid #ddd;'>Price</th>
                        </tr>
                    </thead>
                    <tbody>
        ");

        foreach (var song in songs)
        {
            var songTitle = GetSongTitle(song);
            var imageUrl = GetImageUrl(song, baseUrl);

            sb.Append($@"
                <tr>
                    <td style='padding: 10px; border-bottom: 1px solid #eee;'>
                        <table style='border-collapse: collapse;'>
                            <tr>
                                <td style='width: 50px; vertical-align: middle;'>
                                    {GetImageHtml(imageUrl, 50, 50, "Song Art")}
                                </td>
                                <td style='padding-left: 10px; vertical-align: middle;'>
                                    <span style='color: #333;'>{System.Web.HttpUtility.HtmlEncode(songTitle)}</span>
                                </td>
                            </tr>
                        </table>
                    </td>
                    <td style='padding: 10px; text-align: right; border-bottom: 1px solid #eee; color: #333;'>${song.Price:F2}</td>
                </tr>
            ");
        }

        sb.Append(@"
                    </tbody>
                </table>
            </div>
        ");

        return sb.ToString();
    }

    private string BuildAlbumSection(string albumName, List<CartItemWithMetadata> tracks, string baseUrl)
    {
        var sb = new StringBuilder();

        // Get album cover image from the first track that has an album cover reference
        var albumCoverUrl = GetAlbumCoverUrl(tracks, baseUrl);
        var albumPrice = tracks.Sum(t => t.Price);

        sb.Append($@"
            <div style='margin: 20px 0; border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden;'>
                <div style='background-color: #1a1a2e; padding: 15px;'>
                    <table style='width: 100%; border-collapse: collapse;'>
                        <tr>
                            <td style='width: 80px; vertical-align: middle;'>
                                {GetImageHtml(albumCoverUrl, 80, 80, "Album Art")}
                            </td>
                            <td style='padding-left: 15px; vertical-align: middle;'>
                                <h3 style='margin: 0; color: #ffffff; font-size: 18px;'>{System.Web.HttpUtility.HtmlEncode(albumName)}</h3>
                                <p style='margin: 5px 0 0 0; color: #cccccc;'>Album - ${albumPrice:F2}</p>
                            </td>
                        </tr>
                    </table>
                </div>
                <table style='width: 100%; border-collapse: collapse;'>
                    <thead>
                        <tr style='background-color: #f5f5f5;'>
                            <th style='padding: 10px; text-align: left; border-bottom: 1px solid #ddd;'>#</th>
                            <th style='padding: 10px; text-align: left; border-bottom: 1px solid #ddd;'>Track</th>
                        </tr>
                    </thead>
                    <tbody>
        ");

        // Sort tracks by track number
        var sortedTracks = tracks.OrderBy(t => t.SongMetadata?.TrackNumber ?? 0).ToList();

        foreach (var track in sortedTracks)
        {
            var trackNumber = track.SongMetadata?.TrackNumber ?? 0;
            var trackTitle = GetSongTitle(track);
            var trackImageUrl = GetImageUrl(track, baseUrl);

            sb.Append($@"
                <tr>
                    <td style='padding: 10px; border-bottom: 1px solid #eee; color: #666; width: 40px;'>{trackNumber}</td>
                    <td style='padding: 10px; border-bottom: 1px solid #eee;'>
                        <table style='border-collapse: collapse;'>
                            <tr>
                                <td style='width: 40px; vertical-align: middle;'>
                                    {GetImageHtml(trackImageUrl, 40, 40, "Track Art")}
                                </td>
                                <td style='padding-left: 10px; vertical-align: middle;'>
                                    <span style='color: #333;'>{System.Web.HttpUtility.HtmlEncode(trackTitle)}</span>
                                </td>
                            </tr>
                        </table>
                    </td>
                </tr>
            ");
        }

        sb.Append(@"
                    </tbody>
                </table>
            </div>
        ");

        return sb.ToString();
    }

    private string BuildTotalSection(decimal totalAmount)
    {
        return $@"
            <div style='background-color: #1a1a2e; padding: 15px; border-radius: 8px; margin: 20px 0; text-align: right;'>
                <span style='color: #ffffff; font-size: 18px;'>Total Paid to PayPal:</span>
                <span style='color: #4CAF50; font-size: 24px; font-weight: bold; margin-left: 10px;'>${totalAmount:F2}</span>
            </div>
        ";
    }

    private string BuildSubscriptionDetailsSection(Subscription subscription, string externalSubscriptionId, string? timeZoneId)
    {
        var nextBillingDate = subscription.NextBillingDate.HasValue
            ? UserTimeZoneDisplayHelper.FormatDate(subscription.NextBillingDate.Value, timeZoneId)
            : "Pending";
        var referenceLabel = GetSubscriptionReferenceLabel(subscription.BillingSource);
        var amountLabel = GetSubscriptionAmountLabel(subscription.BillingSource);
        var startDate = UserTimeZoneDisplayHelper.FormatDate(subscription.StartDate, timeZoneId);
        var billingProvider = GetBillingProviderDisplayName(subscription.BillingSource);

        return $@"
            <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                <h3 style='margin: 0 0 15px 0; color: #333; font-size: 18px;'>Subscription Details</h3>
                <table style='width: 100%; border-collapse: collapse;'>
                    <tr>
                        <td style='padding: 8px 0; color: #666;'>Subscription Type:</td>
                        <td style='padding: 8px 0; color: #333; font-weight: bold;'>Monthly Streaming Subscription</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; color: #666;'>Billing Provider:</td>
                        <td style='padding: 8px 0; color: #333;'>{billingProvider}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; color: #666;'>Monthly Price:</td>
                        <td style='padding: 8px 0; color: #333; font-weight: bold;'>${subscription.MonthlyPrice:F2}/month</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; color: #666;'>Start Date:</td>
                        <td style='padding: 8px 0; color: #333;'>{startDate}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; color: #666;'>Next Billing Date:</td>
                        <td style='padding: 8px 0; color: #333;'>{nextBillingDate}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; color: #666;'>{referenceLabel}:</td>
                        <td style='padding: 8px 0; color: #333;'>{System.Web.HttpUtility.HtmlEncode(externalSubscriptionId)}</td>
                    </tr>
                </table>
            </div>
            <div style='background-color: #1a1a2e; padding: 15px; border-radius: 8px; margin: 20px 0; text-align: right;'>
                <span style='color: #ffffff; font-size: 18px;'>{amountLabel}:</span>
                <span style='color: #4CAF50; font-size: 24px; font-weight: bold; margin-left: 10px;'>${subscription.MonthlyPrice:F2}</span>
            </div>
        ";
    }

    private static string GetSubscriptionReferenceLabel(string billingSource)
        => billingSource switch
        {
            BillingSources.GooglePlay => "Google Play Order ID",
            BillingSources.Apple => "App Store Original Transaction ID",
            _ => "PayPal Subscription ID"
        };

    private static string GetSubscriptionAmountLabel(string billingSource)
        => billingSource switch
        {
            BillingSources.GooglePlay => "Amount Charged by Google Play",
            BillingSources.Apple => "Amount Charged by Apple",
            _ => "Amount Paid to PayPal"
        };

    private string BuildSubscriptionTermsSection(Subscription subscription, string? timeZoneId)
    {
        var endDateDisplay = subscription.EndDate.HasValue
            ? UserTimeZoneDisplayHelper.FormatDate(subscription.EndDate.Value, timeZoneId)
            : subscription.NextBillingDate.HasValue
                ? UserTimeZoneDisplayHelper.FormatDate(subscription.NextBillingDate.Value, timeZoneId)
                : UserTimeZoneDisplayHelper.FormatDate(subscription.StartDate.AddMonths(1), timeZoneId);

        return $@"
            <div style='border: 1px solid #e0e0e0; border-radius: 8px; padding: 15px; margin: 20px 0;'>
                <h3 style='margin: 0 0 10px 0; color: #333; font-size: 18px;'>Subscription Terms</h3>
                <ul style='margin: 0; padding-left: 20px; color: #555;'>
                    <li style='margin-bottom: 8px;'>Your subscription renews automatically every month.</li>
                    <li style='margin-bottom: 8px;'><strong>You have the right to cancel at any time.</strong></li>
                    <li style='margin-bottom: 8px;'>If you cancel, your subscription will remain active until your subscription end date: <strong>{endDateDisplay}</strong></li>
                    <li style='margin-bottom: 8px;'>You can manage your subscription in your account settings.</li>
                </ul>
            </div>
        ";
    }

    private string BuildSubscriptionBenefitsSection()
    {
        return @"
            <div style='border: 1px solid #e0e0e0; border-radius: 8px; padding: 15px; margin: 20px 0;'>
                <h3 style='margin: 0 0 10px 0; color: #333; font-size: 18px;'>Your Subscription Benefits</h3>
                <ul style='margin: 0; padding-left: 20px; color: #555;'>
                    <li style='margin-bottom: 8px;'>Unlimited music streaming across the StreamTunes catalog.</li>
                    <li style='margin-bottom: 8px;'>Full access to your playlists and premium listening features.</li>
                    <li style='margin-bottom: 8px;'>The ability to keep listening until the end of any paid billing period if you cancel.</li>
                </ul>
            </div>
        ";
    }

    private static string GetBillingProviderDisplayName(string billingSource)
        => billingSource switch
        {
            BillingSources.GooglePlay => "Google Play",
            BillingSources.Apple => "Apple App Store",
            _ => "PayPal"
        };

    private string BuildEmailFooter()
    {
        return $@"
                <div style='margin-top: 30px; padding-top: 20px; border-top: 1px solid #e0e0e0; text-align: center;'>
                    <p style='color: #666; font-size: 14px;'>Thank you for choosing StreamTunes!</p>
                    <p style='color: #999; font-size: 12px;'>If you have any questions, please contact our support team.</p>
                </div>
            </div>
        </div>
        ";
    }

    private string GetSongTitle(CartItemWithMetadata item)
    {
        // Prefer stored SongTitle from metadata
        if (!string.IsNullOrEmpty(item.SongMetadata?.SongTitle))
        {
            return item.SongMetadata.SongTitle;
        }
        
        // Fall back to extracting from Mp3BlobPath
        if (item.SongMetadata?.Mp3BlobPath != null)
        {
            return Path.GetFileNameWithoutExtension(item.SongMetadata.Mp3BlobPath);
        }

        return Path.GetFileNameWithoutExtension(item.SongFileName);
    }

    private string? GetImageUrl(CartItemWithMetadata item, string baseUrl)
    {
        if (string.IsNullOrEmpty(item.ImageBlobPath))
        {
            return null;
        }

        try
        {
            // Generate a SAS URL for the image that's valid for 7 days (for email viewing)
            var sasUri = _azureStorageService.GetReadSasUri(item.ImageBlobPath, TimeSpan.FromDays(7));
            // Use AbsoluteUri to get properly percent-encoded URL (spaces as %20, not +)
            // This is required for Gmail compatibility which doesn't handle + as space in URLs
            return sasUri.AbsoluteUri;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate SAS URL for image {ImagePath}", item.ImageBlobPath);
            return null;
        }
    }

    /// <summary>
    /// Gets the HTML for displaying an image or a placeholder if no image is available.
    /// Uses table-based layout for maximum email client compatibility (Outlook, Gmail, etc.)
    /// </summary>
    /// <param name="imageUrl">The image URL, or null if no image.</param>
    /// <param name="width">The width of the image/placeholder in pixels.</param>
    /// <param name="height">The height of the image/placeholder in pixels.</param>
    /// <param name="altText">Alt text for the image.</param>
    /// <returns>HTML string for the image or placeholder.</returns>
    private static string GetImageHtml(string? imageUrl, int width, int height, string altText)
    {
        if (!string.IsNullOrEmpty(imageUrl))
        {
            return $"<img src='{imageUrl}' alt='{altText}' style='width: {width}px; height: {height}px; object-fit: cover; border-radius: 4px;' />";
        }

        // Return a styled placeholder with a music note symbol for songs without cover art
        // Uses table-based centering for email client compatibility (Outlook doesn't support flexbox)
        // Uses Unicode music symbol instead of SVG (many email clients strip SVG)
        var fontSize = Math.Max(16, (int)(width * 0.5));
        return $@"<table cellpadding='0' cellspacing='0' border='0' style='width: {width}px; height: {height}px; border-radius: 4px; background-color: #667eea;'>
            <tr>
                <td align='center' valign='middle' style='width: {width}px; height: {height}px; color: #ffffff; font-size: {fontSize}px; font-family: Arial, sans-serif;'>&#9835;</td>
            </tr>
        </table>";
    }

    private string? GetAlbumCoverUrl(List<CartItemWithMetadata> tracks, string baseUrl)
    {
        // Try to find an album cover from the tracks' metadata
        // Album covers have IsAlbumCover = true, but track metadata won't have that
        // So we look for ImageBlobPath on the first track
        var firstTrackWithImage = tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.SongMetadata?.ImageBlobPath));
        if (firstTrackWithImage != null)
        {
            return GetImageUrl(firstTrackWithImage, baseUrl);
        }

        return null;
    }
}
