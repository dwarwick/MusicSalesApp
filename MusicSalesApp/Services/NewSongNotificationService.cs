using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for sending notifications about new songs to users who have opted in.
/// Uses batch sending with delays to avoid spam filter issues.
/// </summary>
public class NewSongNotificationService : INewSongNotificationService
{
    private readonly IEmailService _emailService;
    private readonly ISongMetadataService _songMetadataService;
    private readonly IAzureStorageService _azureStorageService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NewSongNotificationService> _logger;

    // Batch settings to avoid spam filters
    private const int BatchSize = 10; // Send 10 emails at a time
    private const int DelayBetweenBatchesMs = 60000; // 1 minute between batches
    private const int DelayBetweenEmailsMs = 5000; // 5 seconds between individual emails

    public NewSongNotificationService(
        IEmailService emailService,
        ISongMetadataService songMetadataService,
        IAzureStorageService azureStorageService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IConfiguration configuration,
        ILogger<NewSongNotificationService> logger)
    {
        _emailService = emailService;
        _songMetadataService = songMetadataService;
        _azureStorageService = azureStorageService;
        _dbContextFactory = dbContextFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendNewSongNotificationsAsync()
    {
        _logger.LogInformation("Starting new song notification job");

        try
        {
            // Get new songs added in the past 24 hours
            var since = DateTime.UtcNow.AddHours(-24);
            var newSongs = await GetNewSongsAsync(since);

            if (!newSongs.Any())
            {
                _logger.LogInformation("No new songs found in the past 24 hours. Skipping notification emails.");
                return;
            }

            _logger.LogInformation("Found {Count} new items to notify about", newSongs.Count);

            // Get users who have opted in to receive new song emails
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var optedInUsers = await context.Users
                .Where(u => u.ReceiveNewSongEmails && u.EmailConfirmed && !u.IsSuspended)
                .Select(u => new { u.Email, u.UserName })
                .ToListAsync();

            if (!optedInUsers.Any())
            {
                _logger.LogInformation("No users opted in to receive new song notifications.");
                return;
            }

            _logger.LogInformation("Found {Count} users to notify", optedInUsers.Count);

            // Get base URL from configuration
            var baseUrl = _configuration["App:BaseUrl"] ?? "https://streamtunes.net";

            // Generate email content once
            var emailBody = BuildEmailBody(newSongs, baseUrl);
            var subject = newSongs.Count == 1 
                ? "New Music Added - Check it out!" 
                : $"{newSongs.Count} New Songs Added - Check them out!";

            // Send emails in batches to avoid spam filters
            var totalSent = 0;
            var totalFailed = 0;

            for (var i = 0; i < optedInUsers.Count; i += BatchSize)
            {
                var batch = optedInUsers.Skip(i).Take(BatchSize).ToList();
                _logger.LogInformation("Processing batch {BatchNumber} with {Count} users", 
                    (i / BatchSize) + 1, batch.Count);

                foreach (var user in batch)
                {
                    try
                    {
                        var personalizedBody = emailBody.Replace("{USER_NAME}", 
                            string.IsNullOrEmpty(user.UserName) ? "Music Lover" : user.UserName);
                        
                        var sent = await _emailService.SendEmailAsync(user.Email, subject, personalizedBody);
                        
                        if (sent)
                        {
                            totalSent++;
                            _logger.LogDebug("Sent notification to {Email}", user.Email);
                        }
                        else
                        {
                            totalFailed++;
                            _logger.LogWarning("Failed to send notification to {Email}", user.Email);
                        }

                        // Delay between individual emails
                        await Task.Delay(DelayBetweenEmailsMs);
                    }
                    catch (Exception ex)
                    {
                        totalFailed++;
                        _logger.LogError(ex, "Error sending notification to {Email}", user.Email);
                    }
                }

                // Delay between batches (but not after the last batch)
                if (i + BatchSize < optedInUsers.Count)
                {
                    _logger.LogInformation("Waiting {Seconds} seconds before next batch", DelayBetweenBatchesMs / 1000);
                    await Task.Delay(DelayBetweenBatchesMs);
                }
            }

            _logger.LogInformation("Completed new song notification job. Sent: {Sent}, Failed: {Failed}", 
                totalSent, totalFailed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in new song notification job");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<SongMetadata>> GetNewSongsAsync(DateTime since)
    {
        var allSongs = await _songMetadataService.GetAllAsync();
        
        // Get songs added since the specified time
        // For MP3s (songs) - check CreatedAt
        // For album covers - check CreatedAt
        var newItems = allSongs
            .Where(s => s.CreatedAt >= since)
            .ToList();

        return newItems;
    }

    private string BuildEmailBody(List<SongMetadata> newSongs, string baseUrl)
    {
        var logoUrl = $"{baseUrl.TrimEnd('/')}/images/logo-light-small.png";
        var manageAccountUrl = $"{baseUrl.TrimEnd('/')}/manage-account";

        // Group into albums and standalone songs
        var albumCovers = newSongs
            .Where(s => s.IsAlbumCover && !string.IsNullOrEmpty(s.AlbumName))
            .ToList();

        var albumTracks = newSongs
            .Where(s => !s.IsAlbumCover && !string.IsNullOrEmpty(s.AlbumName) && !string.IsNullOrEmpty(s.Mp3BlobPath))
            .GroupBy(s => s.AlbumName)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.TrackNumber ?? 0).ToList());

        var standaloneSongs = newSongs
            .Where(s => !s.IsAlbumCover && string.IsNullOrEmpty(s.AlbumName) && !string.IsNullOrEmpty(s.Mp3BlobPath))
            .ToList();

        var body = new StringBuilder();

        // Email header with logo
        body.Append($@"
        <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
            <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>New Music Alert!</h1>
            </div>
            <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                <p style='font-size: 16px; color: #333;'>Hello {{USER_NAME}},</p>
                <p style='font-size: 16px; color: #333;'>Great news! New music has been added to StreamTunes. Check out what's new:</p>
        ");

        // Albums section
        if (albumCovers.Any())
        {
            body.Append(@"
                <h2 style='color: #1a1a2e; border-bottom: 2px solid #1a1a2e; padding-bottom: 10px; margin-top: 30px;'>New Albums</h2>
            ");

            foreach (var album in albumCovers)
            {
                var albumImageUrl = GetImageUrl(album.ImageBlobPath);
                var tracks = albumTracks.GetValueOrDefault(album.AlbumName) ?? new List<SongMetadata>();

                body.Append($@"
                <div style='margin: 20px 0; border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden;'>
                    <div style='background-color: #1a1a2e; padding: 15px;'>
                        <table style='width: 100%; border-collapse: collapse;'>
                            <tr>
                                <td style='width: 80px; vertical-align: top;'>
                                    {(string.IsNullOrEmpty(albumImageUrl) ? "" : $"<img src='{albumImageUrl}' alt='Album Art' style='width: 80px; height: 80px; object-fit: cover; border-radius: 4px;' />")}
                                </td>
                                <td style='padding-left: 15px; vertical-align: top;'>
                                    <h3 style='margin: 0; color: #ffffff; font-size: 18px;'>{System.Web.HttpUtility.HtmlEncode(album.AlbumName)}</h3>
                                    <p style='margin: 5px 0 0 0; color: #cccccc;'>{tracks.Count} tracks</p>
                                </td>
                            </tr>
                        </table>
                    </div>
                ");

                if (tracks.Any())
                {
                    body.Append(@"
                    <table style='width: 100%; border-collapse: collapse;'>
                        <tbody>
                    ");

                    foreach (var track in tracks)
                    {
                        var trackTitle = Path.GetFileNameWithoutExtension(track.Mp3BlobPath);
                        body.Append($@"
                        <tr>
                            <td style='padding: 10px; border-bottom: 1px solid #eee; color: #666; width: 40px;'>{track.TrackNumber ?? 0}</td>
                            <td style='padding: 10px; border-bottom: 1px solid #eee; color: #333;'>{System.Web.HttpUtility.HtmlEncode(trackTitle)}</td>
                        </tr>
                        ");
                    }

                    body.Append(@"
                        </tbody>
                    </table>
                    ");
                }

                body.Append(@"</div>");
            }
        }

        // Standalone songs section
        if (standaloneSongs.Any())
        {
            body.Append(@"
                <h2 style='color: #1a1a2e; border-bottom: 2px solid #1a1a2e; padding-bottom: 10px; margin-top: 30px;'>New Songs</h2>
                <table style='width: 100%; border-collapse: collapse;'>
                    <tbody>
            ");

            foreach (var song in standaloneSongs)
            {
                var songTitle = Path.GetFileNameWithoutExtension(song.Mp3BlobPath);
                var songImageUrl = GetImageUrl(song.ImageBlobPath);

                body.Append($@"
                <tr>
                    <td style='padding: 10px; border-bottom: 1px solid #eee;'>
                        <table style='border-collapse: collapse;'>
                            <tr>
                                <td style='width: 50px; vertical-align: top;'>
                                    {(string.IsNullOrEmpty(songImageUrl) ? "" : $"<img src='{songImageUrl}' alt='Song Art' style='width: 50px; height: 50px; object-fit: cover; border-radius: 4px;' />")}
                                </td>
                                <td style='padding-left: 10px; vertical-align: middle;'>
                                    <span style='color: #333;'>{System.Web.HttpUtility.HtmlEncode(songTitle)}</span>
                                    {(!string.IsNullOrEmpty(song.Genre) ? $"<br/><span style='color: #666; font-size: 12px;'>{System.Web.HttpUtility.HtmlEncode(song.Genre)}</span>" : "")}
                                </td>
                            </tr>
                        </table>
                    </td>
                </tr>
                ");
            }

            body.Append(@"
                    </tbody>
                </table>
            ");
        }

        // Call to action
        body.Append($@"
                <div style='text-align: center; margin: 30px 0;'>
                    <a href='{baseUrl}' style='display: inline-block; padding: 15px 30px; background-color: #1a1a2e; color: white; text-decoration: none; border-radius: 5px; font-size: 16px;'>Listen Now</a>
                </div>
        ");

        // Footer with manage email preferences link
        body.Append($@"
                <div style='margin-top: 30px; padding-top: 20px; border-top: 1px solid #e0e0e0; text-align: center;'>
                    <p style='color: #666; font-size: 14px;'>Thank you for being a part of StreamTunes!</p>
                    <p style='color: #999; font-size: 12px;'>
                        <a href='{manageAccountUrl}' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
                    </p>
                </div>
            </div>
        </div>
        ");

        return body.ToString();
    }

    private string GetImageUrl(string imageBlobPath)
    {
        if (string.IsNullOrEmpty(imageBlobPath))
        {
            return null;
        }

        try
        {
            // Generate a SAS URL for the image that's valid for 7 days (for email viewing)
            var sasUri = _azureStorageService.GetReadSasUri(imageBlobPath, TimeSpan.FromDays(7));
            return sasUri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate SAS URL for image {ImagePath}", imageBlobPath);
            return null;
        }
    }
}
