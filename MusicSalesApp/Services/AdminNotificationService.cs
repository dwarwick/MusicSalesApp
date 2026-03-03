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
    private readonly ISongMetadataService _songMetadataService;
    private readonly INewSongNotificationService _newSongNotificationService;
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
        ISongMetadataService songMetadataService,
        INewSongNotificationService newSongNotificationService,
        ILogger<AdminNotificationService> logger)
    {
        _emailService = emailService;
        _appSettingsService = appSettingsService;
        _dbContextFactory = dbContextFactory;
        _songMetadataService = songMetadataService;
        _newSongNotificationService = newSongNotificationService;
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
    public async Task NotifyUploadBatchCompletedAsync(string userEmail, int creatorId, List<string> uploadedBlobPaths)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        var userId = user?.Id ?? 0;

        // Record history
        await RecordUserHistoryAsync(userId, userEmail, "UploadCompleted", $"User uploaded {uploadedBlobPaths.Count} file(s)");

        // Look up the uploaded songs by matching exact MP3 blob paths
        var creatorSongs = await _songMetadataService.GetByCreatorIdAsync(creatorId);
        var uploadedPathSet = uploadedBlobPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uploadedSongs = creatorSongs
            .Where(s => !string.IsNullOrEmpty(s.Mp3BlobPath) && uploadedPathSet.Contains(s.Mp3BlobPath))
            .ToList();

        // Send admin email
        if (await IsNotificationEnabledAsync(NotifyUploadCompletedKey))
        {
            var adminSubject = $"StreamTunes Admin - New Upload from {userEmail}";
            var adminBody = BuildUploadSummaryEmailBody(userEmail, uploadedSongs, isAdminEmail: true);
            await SendAdminEmailAsync(adminSubject, adminBody);
        }

        // Send creator confirmation email
        try
        {
            var creatorSubject = "StreamTunes - Your Songs Have Been Uploaded!";
            var creatorBody = BuildUploadSummaryEmailBody(userEmail, uploadedSongs, isAdminEmail: false);
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

        var songCount = uploadedSongs
            .Count(s => !s.IsAlbumCover && !string.IsNullOrEmpty(s.Mp3BlobPath));

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
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Songs Uploaded:</strong> {songCount}</p>
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Date/Time (UTC):</strong> {utcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
                </div>
            ");
        }
        else
        {
            body.Append($@"
                <p style='font-size: 16px; color: #333;'>Great news! Your music has been successfully uploaded to StreamTunes.</p>
                <p style='font-size: 14px; color: #666;'>You uploaded {songCount} song(s). You can manage your songs, update titles, and change cover art from your song management page.</p>
            ");
        }

        // Reuse the daily digest's song list builder for thumbnails + song titles
        body.Append(_newSongNotificationService.BuildSongListHtml(uploadedSongs, "Uploaded Songs"));

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
}
