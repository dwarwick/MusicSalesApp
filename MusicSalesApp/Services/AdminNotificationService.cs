#nullable enable
using System.Text;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for sending admin notification emails and recording user history events.
/// </summary>
public class AdminNotificationService : IAdminNotificationService
{
    private readonly IEmailService _emailService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IAzureStorageService _azureStorageService;
    private readonly ISongMetadataService _songMetadataService;
    private readonly ILogger<AdminNotificationService> _logger;

    public const string AdminEmail = "admin@streamtunes.net";

    // Setting keys for enabling/disabling admin notifications
    public const string NotifyRegistrationKey = "AdminNotify_Registration";
    public const string NotifyEmailConfirmedKey = "AdminNotify_EmailConfirmed";
    public const string NotifyTaxFormCompletedKey = "AdminNotify_TaxFormCompleted";
    public const string NotifyCreatorStatusGainedKey = "AdminNotify_CreatorStatusGained";
    public const string NotifyCreatorStatusLostKey = "AdminNotify_CreatorStatusLost";
    public const string NotifyUploadCompletedKey = "AdminNotify_UploadCompleted";
    public const string NotifySongRenamedKey = "AdminNotify_SongRenamed";
    public const string NotifySongArtUpdatedKey = "AdminNotify_SongArtUpdated";

    public AdminNotificationService(
        IEmailService emailService,
        IAppSettingsService appSettingsService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IAzureStorageService azureStorageService,
        ISongMetadataService songMetadataService,
        ILogger<AdminNotificationService> logger)
    {
        _emailService = emailService;
        _appSettingsService = appSettingsService;
        _dbContextFactory = dbContextFactory;
        _azureStorageService = azureStorageService;
        _songMetadataService = songMetadataService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyUserRegisteredAsync(string userEmail)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        var userId = user?.Id ?? 0;

        await RecordUserHistoryAsync(userId, userEmail, "Registration", $"User registered: {userEmail}");

        if (!await IsNotificationEnabledAsync(NotifyRegistrationKey))
            return;

        var subject = "StreamTunes Admin - New User Registration";
        var body = BuildAdminEmailBody("New User Registration",
            $"A new user has registered on StreamTunes.",
            userEmail);
        await SendAdminEmailAsync(subject, body);
    }

    /// <inheritdoc />
    public async Task NotifyEmailConfirmedAsync(string userEmail)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        var userId = user?.Id ?? 0;

        await RecordUserHistoryAsync(userId, userEmail, "EmailConfirmed", $"User confirmed email: {userEmail}");

        if (!await IsNotificationEnabledAsync(NotifyEmailConfirmedKey))
            return;

        var subject = "StreamTunes Admin - Email Confirmed";
        var body = BuildAdminEmailBody("Email Confirmed",
            $"A user has confirmed their email address.",
            userEmail);
        await SendAdminEmailAsync(subject, body);
    }

    /// <inheritdoc />
    public async Task NotifyTaxFormCompletedAsync(string userEmail, string formType)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        var userId = user?.Id ?? 0;

        await RecordUserHistoryAsync(userId, userEmail, "TaxFormCompleted", $"User completed {formType} tax form: {userEmail}");

        if (!await IsNotificationEnabledAsync(NotifyTaxFormCompletedKey))
            return;

        var subject = $"StreamTunes Admin - {formType} Tax Form Completed";
        var body = BuildAdminEmailBody($"{formType} Tax Form Completed",
            $"A user has completed their {formType} tax form.",
            userEmail);
        await SendAdminEmailAsync(subject, body);
    }

    /// <inheritdoc />
    public async Task NotifyCreatorStatusGainedAsync(string userEmail)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        var userId = user?.Id ?? 0;

        await RecordUserHistoryAsync(userId, userEmail, "CreatorStatusGained", $"User gained creator status: {userEmail}", "Non-Creator", "Creator");

        if (!await IsNotificationEnabledAsync(NotifyCreatorStatusGainedKey))
            return;

        var subject = "StreamTunes Admin - Creator Status Gained";
        var body = BuildAdminEmailBody("Creator Status Gained",
            $"A user has gained creator status.",
            userEmail);
        await SendAdminEmailAsync(subject, body);
    }

    /// <inheritdoc />
    public async Task NotifyCreatorStatusLostAsync(string userEmail)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        var userId = user?.Id ?? 0;

        await RecordUserHistoryAsync(userId, userEmail, "CreatorStatusLost", $"User lost creator status: {userEmail}", "Creator", "Non-Creator");

        if (!await IsNotificationEnabledAsync(NotifyCreatorStatusLostKey))
            return;

        var subject = "StreamTunes Admin - Creator Status Lost";
        var body = BuildAdminEmailBody("Creator Status Lost",
            $"A user has lost their creator status.",
            userEmail);
        await SendAdminEmailAsync(subject, body);
    }

    /// <inheritdoc />
    public async Task NotifyUploadBatchCompletedAsync(string userEmail, int creatorId, List<string> uploadedFileNames)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        var userId = user?.Id ?? 0;

        // Record history for each uploaded file
        var fileList = string.Join(", ", uploadedFileNames);
        await RecordUserHistoryAsync(userId, userEmail, "UploadCompleted", $"User uploaded {uploadedFileNames.Count} file(s): {fileList}");

        // Look up the recently uploaded songs for this creator to build the summary email
        var creatorSongs = await _songMetadataService.GetByCreatorIdAsync(creatorId);
        // Match uploaded songs by comparing normalized filenames against uploaded file list
        var normalizedUploadedNames = uploadedFileNames
            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
            .ToHashSet();
        var recentSongs = creatorSongs
            .Where(s => !string.IsNullOrEmpty(s.Mp3BlobPath) &&
                        normalizedUploadedNames.Contains(
                            Path.GetFileNameWithoutExtension(s.Mp3BlobPath).ToLowerInvariant()))
            .ToList();

        // Send admin email
        if (await IsNotificationEnabledAsync(NotifyUploadCompletedKey))
        {
            var adminSubject = $"StreamTunes Admin - New Upload from {userEmail}";
            var adminBody = BuildUploadSummaryEmailBody(userEmail, recentSongs, isAdminEmail: true);
            await SendAdminEmailAsync(adminSubject, adminBody);
        }

        // Send creator confirmation email
        try
        {
            var creatorSubject = "StreamTunes - Your Songs Have Been Uploaded!";
            var creatorBody = BuildUploadSummaryEmailBody(userEmail, recentSongs, isAdminEmail: false);
            await _emailService.SendEmailAsync(userEmail, creatorSubject, creatorBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send upload confirmation email to creator {Email}", userEmail);
        }
    }

    /// <inheritdoc />
    public async Task NotifySongRenamedAsync(string userEmail, string oldTitle, string newTitle)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        var userId = user?.Id ?? 0;

        await RecordUserHistoryAsync(userId, userEmail, "SongRenamed", $"User renamed song from '{oldTitle}' to '{newTitle}'", oldTitle, newTitle);

        if (!await IsNotificationEnabledAsync(NotifySongRenamedKey))
            return;

        var subject = "StreamTunes Admin - Song Renamed";
        var body = BuildAdminEmailBody("Song Renamed",
            $"A user has renamed a song from '{System.Web.HttpUtility.HtmlEncode(oldTitle)}' to '{System.Web.HttpUtility.HtmlEncode(newTitle)}'.",
            userEmail);
        await SendAdminEmailAsync(subject, body);
    }

    /// <inheritdoc />
    public async Task NotifySongArtUpdatedAsync(string userEmail, string songTitle)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        var userId = user?.Id ?? 0;

        await RecordUserHistoryAsync(userId, userEmail, "SongArtUpdated", $"User updated song art for: {songTitle}");

        if (!await IsNotificationEnabledAsync(NotifySongArtUpdatedKey))
            return;

        var subject = "StreamTunes Admin - Song Art Updated";
        var body = BuildAdminEmailBody("Song Art Updated",
            $"A user has updated the cover art for song '{System.Web.HttpUtility.HtmlEncode(songTitle)}'.",
            userEmail);
        await SendAdminEmailAsync(subject, body);
    }

    /// <inheritdoc />
    public async Task RecordUserHistoryAsync(int userId, string userEmail, string eventType, string description, string? oldValue = null, string? newValue = null)
    {
        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var history = new UserHistory
            {
                UserId = userId,
                UserEmail = userEmail,
                EventType = eventType,
                Description = description,
                OldValue = oldValue,
                NewValue = newValue
            };
            context.UserHistories.Add(history);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record user history for {UserEmail}, event {EventType}", userEmail, eventType);
        }
    }

    /// <inheritdoc />
    public async Task<List<UserHistory>> GetAllUserHistoryAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.UserHistories
            .Include(h => h.User)
            .OrderByDescending(h => h.OccurredAt)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<bool> IsNotificationEnabledAsync(string settingKey)
    {
        var value = await _appSettingsService.GetSettingAsync(settingKey);
        // Default to enabled if not set
        if (string.IsNullOrEmpty(value))
            return true;
        return bool.TryParse(value, out var enabled) && enabled;
    }

    /// <inheritdoc />
    public async Task SetNotificationEnabledAsync(string settingKey, bool enabled)
    {
        await _appSettingsService.SetSettingAsync(settingKey, enabled.ToString(), $"Admin notification: {settingKey}");
    }

    private async Task SendAdminEmailAsync(string subject, string body)
    {
        try
        {
            await _emailService.SendEmailAsync(AdminEmail, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send admin notification email: {Subject}", subject);
        }
    }

    private string BuildAdminEmailBody(string title, string message, string userEmail)
    {
        var logoUrl = _emailService.GetLogoUrl();
        var utcNow = DateTime.UtcNow;

        return $@"
        <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
            <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>{title}</h1>
            </div>
            <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                <p style='font-size: 16px; color: #333;'>{message}</p>
                <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>User Email:</strong> {System.Web.HttpUtility.HtmlEncode(userEmail)}</p>
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Date/Time (UTC):</strong> {utcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
                </div>
                <div style='margin-top: 30px; padding-top: 20px; border-top: 1px solid #e0e0e0; text-align: center;'>
                    <p style='color: #999; font-size: 12px;'>This is an automated admin notification from StreamTunes.</p>
                </div>
            </div>
        </div>";
    }

    private string BuildUploadSummaryEmailBody(string creatorEmail, List<SongMetadata> uploadedSongs, bool isAdminEmail)
    {
        var logoUrl = _emailService.GetLogoUrl();
        var baseUrl = _emailService.GetAppBaseUrl();
        var utcNow = DateTime.UtcNow;

        // Separate standalone songs (with MP3) from album cover entries
        var standaloneSongs = uploadedSongs
            .Where(s => !s.IsAlbumCover && !string.IsNullOrEmpty(s.Mp3BlobPath))
            .ToList();

        var body = new StringBuilder();

        // Email header with logo
        var title = isAdminEmail ? "New Upload" : "Your Songs Have Been Uploaded!";
        body.Append($@"
        <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
            <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>{title}</h1>
            </div>
            <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
        ");

        if (isAdminEmail)
        {
            body.Append($@"
                <p style='font-size: 16px; color: #333;'>A creator has uploaded new music to StreamTunes.</p>
                <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Creator Email:</strong> {System.Web.HttpUtility.HtmlEncode(creatorEmail)}</p>
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Songs Uploaded:</strong> {standaloneSongs.Count}</p>
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Date/Time (UTC):</strong> {utcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
                </div>
            ");
        }
        else
        {
            body.Append($@"
                <p style='font-size: 16px; color: #333;'>Great news! Your music has been successfully uploaded to StreamTunes.</p>
                <p style='font-size: 14px; color: #666;'>You uploaded {standaloneSongs.Count} song(s). You can manage your songs, update titles, and change cover art from your song management page.</p>
            ");
        }

        // Songs table with thumbnails
        if (standaloneSongs.Any())
        {
            body.Append(@"
                <h2 style='color: #1a1a2e; border-bottom: 2px solid #1a1a2e; padding-bottom: 10px; margin-top: 30px;'>Uploaded Songs</h2>
                <table style='width: 100%; border-collapse: collapse;'>
                    <tbody>
            ");

            foreach (var song in standaloneSongs)
            {
                var songTitle = !string.IsNullOrEmpty(song.SongTitle)
                    ? song.SongTitle
                    : Path.GetFileNameWithoutExtension(song.Mp3BlobPath ?? "Unknown");
                var songImageUrl = GetImageUrl(song.ImageBlobPath);

                body.Append($@"
                <tr>
                    <td style='padding: 10px; border-bottom: 1px solid #eee;'>
                        <table style='border-collapse: collapse;'>
                            <tr>
                                <td style='width: 60px; vertical-align: top;'>
                                    {GetImageHtml(songImageUrl, 60, 60, "Cover Art")}
                                </td>
                                <td style='padding-left: 10px; vertical-align: middle;'>
                                    <span style='color: #333; font-size: 14px; font-weight: bold;'>{System.Web.HttpUtility.HtmlEncode(songTitle)}</span>
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
        if (!isAdminEmail)
        {
            var manageSongsUrl = $"{baseUrl.TrimEnd('/')}/creator/songs";
            body.Append($@"
                <div style='text-align: center; margin: 30px 0;'>
                    <a href='{manageSongsUrl}' style='display: inline-block; padding: 15px 30px; background-color: #1a1a2e; color: white; text-decoration: none; border-radius: 5px; font-size: 16px;'>Manage My Songs</a>
                </div>
            ");
        }

        // Footer
        body.Append($@"
                <div style='margin-top: 30px; padding-top: 20px; border-top: 1px solid #e0e0e0; text-align: center;'>
                    <p style='color: #999; font-size: 12px;'>{(isAdminEmail ? "This is an automated admin notification from StreamTunes." : "Thank you for being a creator on StreamTunes!")}</p>
                </div>
            </div>
        </div>
        ");

        return body.ToString();
    }

    private string? GetImageUrl(string? imageBlobPath)
    {
        if (string.IsNullOrEmpty(imageBlobPath))
            return null;

        try
        {
            var sasUri = _azureStorageService.GetReadSasUri(imageBlobPath, TimeSpan.FromDays(7));
            return sasUri.AbsoluteUri;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate SAS URL for image {ImagePath}", imageBlobPath);
            return null;
        }
    }

    private static string GetImageHtml(string? imageUrl, int width, int height, string altText)
    {
        if (!string.IsNullOrEmpty(imageUrl))
        {
            return $"<img src='{imageUrl}' alt='{altText}' style='width: {width}px; height: {height}px; object-fit: cover; border-radius: 4px;' />";
        }

        var fontSize = Math.Max(16, (int)(width * 0.5));
        return $@"<table cellpadding='0' cellspacing='0' border='0' style='width: {width}px; height: {height}px; border-radius: 4px; background-color: #667eea;'>
            <tr>
                <td align='center' valign='middle' style='width: {width}px; height: {height}px; color: #ffffff; font-size: {fontSize}px; font-family: Arial, sans-serif;'>&#9835;</td>
            </tr>
        </table>";
    }
}
