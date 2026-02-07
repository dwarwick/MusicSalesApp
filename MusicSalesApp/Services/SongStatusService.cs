using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing song enable/disable status
/// </summary>
public class SongStatusService : ISongStatusService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<SongStatusService> _logger;
    private readonly IEmailService _emailService;
    private readonly IAzureStorageService _azureStorageService;

    private const string CustomerServiceEmail = "customerservice@streamtunes.net";

    public SongStatusService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<SongStatusService> logger,
        IEmailService emailService,
        IAzureStorageService azureStorageService)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _emailService = emailService;
        _azureStorageService = azureStorageService;
    }

    public async Task<bool> DisableSongAsync(int songMetadataId, string reason, int adminUserId, string baseUrl)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Get the song metadata with creator info
            var song = await context.SongMetadata
                .Include(s => s.Creator)
                .ThenInclude(c => c.User)
                .FirstOrDefaultAsync(s => s.Id == songMetadataId);

            if (song == null)
            {
                _logger.LogWarning("Song {SongMetadataId} not found when trying to disable", songMetadataId);
                return false;
            }

            // Update the song status
            song.IsEnabled = false;
            song.StatusReason = reason;
            song.UpdatedAt = DateTime.UtcNow;

            // Add status history record
            var historyRecord = new SongStatusHistory
            {
                SongMetadataId = songMetadataId,
                IsEnabled = false,
                Reason = reason,
                ChangedAt = DateTime.UtcNow,
                ChangedByUserId = adminUserId
            };
            context.SongStatusHistories.Add(historyRecord);

            // Remove the song from all playlists
            var playlistEntries = await context.UserPlaylists
                .Where(up => up.SongMetadataId == songMetadataId)
                .ToListAsync();

            if (playlistEntries.Any())
            {
                context.UserPlaylists.RemoveRange(playlistEntries);
                _logger.LogInformation("Removed song {SongMetadataId} from {Count} playlists", songMetadataId, playlistEntries.Count);
            }

            await context.SaveChangesAsync();

            // Send notification email to creator if they have an email
            if (song.Creator?.User?.Email != null)
            {
                await SendDisableNotificationEmailAsync(song, reason, baseUrl);
            }

            _logger.LogInformation("Song {SongMetadataId} disabled by admin {AdminUserId}. Reason: {Reason}", songMetadataId, adminUserId, reason);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disabling song {SongMetadataId}", songMetadataId);
            throw;
        }
    }

    public async Task<bool> EnableSongAsync(int songMetadataId, string reason, int adminUserId, string baseUrl)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Get the song metadata with creator info
            var song = await context.SongMetadata
                .Include(s => s.Creator)
                .ThenInclude(c => c.User)
                .FirstOrDefaultAsync(s => s.Id == songMetadataId);

            if (song == null)
            {
                _logger.LogWarning("Song {SongMetadataId} not found when trying to enable", songMetadataId);
                return false;
            }

            // Update the song status
            song.IsEnabled = true;
            song.StatusReason = reason; // Store the reason for status change
            song.UpdatedAt = DateTime.UtcNow;

            // Add status history record
            var historyRecord = new SongStatusHistory
            {
                SongMetadataId = songMetadataId,
                IsEnabled = true,
                Reason = reason,
                ChangedAt = DateTime.UtcNow,
                ChangedByUserId = adminUserId
            };
            context.SongStatusHistories.Add(historyRecord);

            await context.SaveChangesAsync();

            // Send notification email to creator if they have an email
            if (song.Creator?.User?.Email != null)
            {
                await SendEnableNotificationEmailAsync(song, reason, baseUrl);
            }

            _logger.LogInformation("Song {SongMetadataId} enabled by admin {AdminUserId}. Reason: {Reason}", songMetadataId, adminUserId, reason);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enabling song {SongMetadataId}", songMetadataId);
            throw;
        }
    }

    public async Task<List<SongStatusHistory>> GetSongStatusHistoryAsync(int songMetadataId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongStatusHistories
                .Include(h => h.ChangedByUser)
                .Where(h => h.SongMetadataId == songMetadataId)
                .OrderByDescending(h => h.ChangedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status history for song {SongMetadataId}", songMetadataId);
            throw;
        }
    }

    public async Task<List<SongStatusHistory>> GetAllStatusHistoryAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongStatusHistories
                .Include(h => h.SongMetadata)
                    .ThenInclude(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Include(h => h.ChangedByUser)
                .OrderByDescending(h => h.ChangedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all status history");
            throw;
        }
    }

    private async Task SendDisableNotificationEmailAsync(SongMetadata song, string reason, string baseUrl)
    {
        try
        {
            var creatorEmail = song.Creator?.User?.Email;
            if (string.IsNullOrEmpty(creatorEmail))
            {
                _logger.LogWarning("Cannot send disable notification - no email for creator of song {SongMetadataId}", song.Id);
                return;
            }

            var songTitle = !string.IsNullOrEmpty(song.SongTitle) 
                ? song.SongTitle 
                : Path.GetFileNameWithoutExtension(song.Mp3BlobPath ?? song.BlobPath);

            var logoUrl = _emailService.GetLogoUrl();
            
            // Get song image URL if available
            string songImageHtml = "";
            if (!string.IsNullOrEmpty(song.ImageBlobPath))
            {
                try
                {
                    var imageUrl = _azureStorageService.GetReadSasUri(song.ImageBlobPath, TimeSpan.FromDays(1)).ToString();
                    songImageHtml = $"<div style='text-align: center; margin: 20px 0;'><img src='{imageUrl}' alt='Song cover' style='max-width: 200px; border-radius: 8px;' /></div>";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate image URL for song {SongMetadataId}", song.Id);
                }
            }

            var subject = $"Your Song Has Been Disabled - {songTitle}";
            var body = $@"
                <div style='text-align: center; margin-bottom: 20px;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                </div>
                <h2>Song Disabled</h2>
                <p>We are writing to inform you that your song <strong>""{songTitle}""</strong> has been disabled on StreamTunes.</p>
                {songImageHtml}
                <h3>Reason for Disabling</h3>
                <p style='background-color: #f8f9fa; padding: 15px; border-radius: 5px; border-left: 4px solid #dc3545;'>{reason}</p>
                <h3>What This Means</h3>
                <ul>
                    <li>Your song will no longer appear in the media library</li>
                    <li>Your song has been removed from all user playlists</li>
                    <li>Your song cannot be played on the platform</li>
                    <li>You will not earn royalties for streams while the song is disabled</li>
                </ul>
                <h3>Questions or Concerns?</h3>
                <p>If you believe this action was taken in error, or if you would like to discuss this decision, please contact our customer service team:</p>
                <p><a href='mailto:{CustomerServiceEmail}' style='display: inline-block; padding: 10px 20px; background-color: #1a1a2e; color: white; text-decoration: none; border-radius: 5px;'>Contact Customer Service</a></p>
                <p style='color: #666;'>Email: <a href='mailto:{CustomerServiceEmail}'>{CustomerServiceEmail}</a></p>
                <p style='color: #999; font-size: 12px; margin-top: 30px;'>
                    <a href='{baseUrl.TrimEnd('/')}/manage-account' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
                </p>
            ";

            await _emailService.SendEmailAsync(creatorEmail, subject, body);
            _logger.LogInformation("Sent disable notification email to creator {Email} for song {SongMetadataId}", creatorEmail, song.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send disable notification email for song {SongMetadataId}", song.Id);
        }
    }

    private async Task SendEnableNotificationEmailAsync(SongMetadata song, string reason, string baseUrl)
    {
        try
        {
            var creatorEmail = song.Creator?.User?.Email;
            if (string.IsNullOrEmpty(creatorEmail))
            {
                _logger.LogWarning("Cannot send enable notification - no email for creator of song {SongMetadataId}", song.Id);
                return;
            }

            var songTitle = !string.IsNullOrEmpty(song.SongTitle) 
                ? song.SongTitle 
                : Path.GetFileNameWithoutExtension(song.Mp3BlobPath ?? song.BlobPath);

            var logoUrl = _emailService.GetLogoUrl();
            
            // Get song image URL if available
            string songImageHtml = "";
            if (!string.IsNullOrEmpty(song.ImageBlobPath))
            {
                try
                {
                    var imageUrl = _azureStorageService.GetReadSasUri(song.ImageBlobPath, TimeSpan.FromDays(1)).ToString();
                    songImageHtml = $"<div style='text-align: center; margin: 20px 0;'><img src='{imageUrl}' alt='Song cover' style='max-width: 200px; border-radius: 8px;' /></div>";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate image URL for song {SongMetadataId}", song.Id);
                }
            }

            var subject = $"Your Song Has Been Re-Enabled - {songTitle}";
            var body = $@"
                <div style='text-align: center; margin-bottom: 20px;'>
                    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                </div>
                <h2>Song Re-Enabled</h2>
                <p>Great news! Your song <strong>""{songTitle}""</strong> has been re-enabled on StreamTunes.</p>
                {songImageHtml}
                <h3>Reason for Re-Enabling</h3>
                <p style='background-color: #f8f9fa; padding: 15px; border-radius: 5px; border-left: 4px solid #28a745;'>{reason}</p>
                <h3>What This Means</h3>
                <ul>
                    <li>Your song is now visible in the media library</li>
                    <li>Users can add your song to their playlists</li>
                    <li>Your song can be played on the platform</li>
                    <li>You will earn royalties for streams of this song</li>
                </ul>
                <h3>Questions or Concerns?</h3>
                <p>If you have any questions, please contact our customer service team:</p>
                <p><a href='mailto:{CustomerServiceEmail}' style='display: inline-block; padding: 10px 20px; background-color: #28a745; color: white; text-decoration: none; border-radius: 5px;'>Contact Customer Service</a></p>
                <p style='color: #666;'>Email: <a href='mailto:{CustomerServiceEmail}'>{CustomerServiceEmail}</a></p>
                <p style='color: #999; font-size: 12px; margin-top: 30px;'>
                    <a href='{baseUrl.TrimEnd('/')}/manage-account' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
                </p>
            ";

            await _emailService.SendEmailAsync(creatorEmail, subject, body);
            _logger.LogInformation("Sent enable notification email to creator {Email} for song {SongMetadataId}", creatorEmail, song.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send enable notification email for song {SongMetadataId}", song.Id);
        }
    }
}
