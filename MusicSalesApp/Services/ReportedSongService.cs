#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class ReportedSongService : IReportedSongService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IEmailService _emailService;
    private readonly ILogger<ReportedSongService> _logger;

    public ReportedSongService(
        IDbContextFactory<AppDbContext> contextFactory,
        IEmailService emailService,
        ILogger<ReportedSongService> logger)
    {
        _contextFactory = contextFactory;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<ReportedSong> ReportSongAsync(int reportingUserId, int songMetadataId, string reason)
    {
        if (!ReportReasonTypes.All.Contains(reason))
            throw new ArgumentException($"Invalid report reason: {reason}", nameof(reason));

        await using var context = await _contextFactory.CreateDbContextAsync();

        var song = await context.SongMetadata
            .Include(s => s.Creator)
            .FirstOrDefaultAsync(s => s.Id == songMetadataId)
            ?? throw new InvalidOperationException($"Song with ID {songMetadataId} not found.");

        var reportingUser = await context.Users
            .FirstOrDefaultAsync(u => u.Id == reportingUserId)
            ?? throw new InvalidOperationException($"User with ID {reportingUserId} not found.");

        var alreadyReported = await context.ReportedSongs
            .AnyAsync(rs => rs.SongMetadataId == songMetadataId && rs.ReportingUserId == reportingUserId);
        if (alreadyReported)
            throw new InvalidOperationException("You have already reported this song.");

        var report = new ReportedSong
        {
            SongMetadataId = songMetadataId,
            ReportingUserId = reportingUserId,
            Reason = reason,
            CreatedAt = DateTime.UtcNow
        };

        context.ReportedSongs.Add(report);
        await context.SaveChangesAsync();

        await SendReportNotificationEmailsAsync(song, reportingUser, reason);

        return report;
    }

    public async Task<List<ReportedSong>> GetAllReportsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.ReportedSongs
            .Include(rs => rs.SongMetadata)
                .ThenInclude(sm => sm.Creator)
                    .ThenInclude(c => c!.User)
            .Include(rs => rs.ReportingUser)
            .OrderByDescending(rs => rs.CreatedAt)
            .ToListAsync();
    }

    public async Task<bool> ResolveReportAsync(int reportId, bool accepted)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var report = await context.ReportedSongs.FindAsync(reportId);
        if (report == null)
            return false;

        report.ResolutionDateTime = DateTime.UtcNow;
        report.ResolutionAccepted = accepted;

        await context.SaveChangesAsync();
        return true;
    }

    private async Task SendReportNotificationEmailsAsync(SongMetadata song, ApplicationUser reportingUser, string reason)
    {
        var effectiveTitle = SongTitleHelper.GetEffectiveTitle(
            song.SongTitle, song.Mp3BlobPath, song.BlobPath);
        var songTitle = System.Web.HttpUtility.HtmlEncode(effectiveTitle);
        var encodedReason = System.Web.HttpUtility.HtmlEncode(reason);

        // 1. Send email to admin
        await SendEmailSafeAsync(
            AdminNotificationService.AdminEmail,
            $"StreamTunes Admin - Song Reported: {effectiveTitle}",
            BuildEmailBody("Song Reported — Admin Notice",
                $"A song has been reported by a user for review.",
                songTitle, encodedReason, reportingUser.Email ?? "Unknown"));

        // 2. Send email to creator (if creator exists)
        if (song.Creator != null)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var creatorUser = await context.Users.FirstOrDefaultAsync(u => u.Id == song.Creator.UserId);
            if (creatorUser?.Email != null)
            {
                await SendEmailSafeAsync(
                    creatorUser.Email,
                    $"StreamTunes - Your Song Has Been Reported",
                    BuildEmailBody("Your Song Has Been Reported",
                        $"One of your songs has been reported for a potential policy violation. Our team will review this report. No action is required from you at this time.",
                        songTitle, encodedReason, null));
            }
        }

        // 3. Send receipt email to the reporting user
        if (reportingUser.Email != null)
        {
            await SendEmailSafeAsync(
                reportingUser.Email,
                $"StreamTunes - Report Received",
                BuildEmailBody("Report Received",
                    $"Thank you for your report. Our team will review the content and take appropriate action.",
                    songTitle, encodedReason, null));
        }
    }

    private string BuildEmailBody(string title, string message, string songTitle, string reason, string? reporterEmail)
    {
        var logoUrl = _emailService.GetLogoUrl();
        var utcNow = DateTime.UtcNow;

        var reporterLine = reporterEmail != null
            ? $"<p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Reported by:</strong> {System.Web.HttpUtility.HtmlEncode(reporterEmail)}</p>"
            : "";

        return $@"
        <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
            <div style='text-align: center; padding: 20px; background-color: #8b0000; border-radius: 8px 8px 0 0;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>{title}</h1>
            </div>
            <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                <p style='font-size: 16px; color: #333;'>{message}</p>
                <div style='background-color: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Song:</strong> {songTitle}</p>
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Reason:</strong> {reason}</p>
                    {reporterLine}
                    <p style='font-size: 14px; color: #333; margin: 5px 0;'><strong>Date/Time (UTC):</strong> {utcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
                </div>
                <div style='margin-top: 30px; padding-top: 20px; border-top: 1px solid #e0e0e0; text-align: center;'>
                    <p style='color: #999; font-size: 12px;'>This is an automated notification from StreamTunes.</p>
                </div>
            </div>
        </div>";
    }

    private async Task SendEmailSafeAsync(string toEmail, string subject, string body)
    {
        try
        {
            await _emailService.SendEmailAsync(toEmail, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send report notification email to {Email}: {Subject}", toEmail, subject);
        }
    }
}
