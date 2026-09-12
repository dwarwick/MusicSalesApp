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
    private readonly ISongStatusService _songStatusService;
    private readonly ILogger<ReportedSongService> _logger;

    public ReportedSongService(
        IDbContextFactory<AppDbContext> contextFactory,
        IEmailService emailService,
        ISongStatusService songStatusService,
        ILogger<ReportedSongService> logger)
    {
        _contextFactory = contextFactory;
        _emailService = emailService;
        // Deciding a report is a song-status change, so it goes through the same service the admin
        // song screen uses. That is what writes SongStatusHistory, strips the song out of every
        // playlist and mails the creator - all of which a local IsEnabled = false would skip.
        _songStatusService = songStatusService;
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

        // A creator reporting their own song has no meaning - the moderation queue exists to tell
        // an admin what strangers object to, and the creator already has a takedown of their own in
        // the creator pages. Left open it is also a way to have your own song disabled by someone
        // who has borrowed your account, and a way to flood the queue with rows nobody can action.
        //
        // Creator may be null (a song whose creator row has gone), and that reads as "not mine",
        // which is the safe direction here: the report is allowed and an admin sees it.
        if (song.Creator is { } creator && creator.UserId == reportingUserId)
            throw new SelfReportNotAllowedException();

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

    public async Task<ReportResolutionResult> ResolveReportAsync(int reportId, bool accepted, int adminUserId, string baseUrl)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var report = await LoadReportWithPeopleAsync(context, reportId);
        if (report == null)
            return new ReportResolutionResult(false, false, "That report no longer exists.");

        // Nothing is checked here on purpose. "Is it still pending" is answered by the conditional
        // UPDATE inside ApplyDecisionAsync, because checking here and writing there are two
        // statements and two admins with the queue open both pass the check.
        return await ApplyDecisionAsync(context, report, accepted, adminUserId, baseUrl, adminReason: null, isReversal: false);
    }

    public async Task<ReportResolutionResult> ReverseDecisionAsync(int reportId, string reason, int adminUserId, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required when reversing a decision.", nameof(reason));

        await using var context = await _contextFactory.CreateDbContextAsync();

        var report = await LoadReportWithPeopleAsync(context, reportId);
        if (report == null)
            return new ReportResolutionResult(false, false, "That report no longer exists.");

        // Unlike the pending check, this one is safe to make here: it reads the decision that the
        // conditional UPDATE below then pins, so a concurrent change makes that UPDATE match
        // nothing rather than letting this reverse a decision that is no longer there.
        if (report.ResolutionDateTime == null || report.ResolutionAccepted == null)
        {
            return new ReportResolutionResult(false, false,
                "That report has not been decided yet, so there is nothing to reverse.");
        }

        // Reversing flips the decision rather than returning the report to the queue. An appeal
        // that wins has already been judged - sending it back to Pending would only ask somebody
        // to decide the same thing over again.
        return await ApplyDecisionAsync(
            context,
            report,
            accepted: !report.ResolutionAccepted.Value,
            adminUserId,
            baseUrl,
            adminReason: reason.Trim(),
            isReversal: true);
    }

    private static async Task<ReportedSong?> LoadReportWithPeopleAsync(AppDbContext context, int reportId)
    {
        return await context.ReportedSongs
            .Include(r => r.SongMetadata)
                .ThenInclude(s => s.Creator)
                    .ThenInclude(c => c!.User)
            .Include(r => r.ReportingUser)
            .FirstOrDefaultAsync(r => r.Id == reportId);
    }

    /// <summary>
    /// Claims the decision, then carries it out. The claim is a conditional UPDATE rather than a
    /// read followed by a write: it is the only step that can pick a winner when two admins decide
    /// the same report at once, and losing it is how one song would otherwise get two takedowns,
    /// two history rows and two emails to its creator. If the song's status then cannot be changed,
    /// the claim is rolled back so the report is left exactly as it was and can be retried.
    /// </summary>
    private async Task<ReportResolutionResult> ApplyDecisionAsync(
        AppDbContext context,
        ReportedSong report,
        bool accepted,
        int adminUserId,
        string baseUrl,
        string? adminReason,
        bool isReversal)
    {
        var song = report.SongMetadata;
        var songTitle = song == null
            ? "Unknown"
            : SongTitleHelper.GetEffectiveTitle(song.SongTitle, song.Mp3BlobPath, song.BlobPath);

        var statusReason = BuildStatusReason(report.Reason, accepted, adminReason, isReversal);
        var previousDecision = report.ResolutionAccepted;
        var previousDecidedAt = report.ResolutionDateTime;
        var decidedAt = DateTime.UtcNow;

        var claimedTarget = await ClaimAsync(
            context.ReportedSongs.Where(r => r.Id == report.Id),
            previousDecision, isReversal, accepted, decidedAt);

        if (claimedTarget == 0)
        {
            return new ReportResolutionResult(false, false, isReversal
                ? "Someone else changed that decision first. Reload the page to see where it stands."
                : "That report has already been decided. Reverse the decision if it needs to change.");
        }

        // A report is a complaint that one song broke one rule, so the decision belongs to every
        // other report saying the same thing about the same song. Left in the queue they would ask
        // an admin to decide a song that is already decided.
        var siblings = await FindSiblingReportsAsync(context, report, previousDecision, isReversal);
        var siblingIds = siblings.Select(r => r.Id).ToList();
        var claimedSiblings = siblingIds.Count == 0
            ? 0
            : await ClaimAsync(
                context.ReportedSongs.Where(r => siblingIds.Contains(r.Id)),
                previousDecision, isReversal, accepted, decidedAt);

        var songDisabled = false;
        var songReEnabled = false;
        var creatorAlreadyNotified = false;
        var songMissing = song == null;
        var heldDownByAnotherFinding = false;

        if (song != null)
        {
            try
            {
                if (accepted && song.IsEnabled)
                {
                    // The bool matters: false means the song row was gone by the time the status
                    // service looked, so nobody was emailed and nothing was recorded.
                    songDisabled = await _songStatusService.DisableSongAsync(song.Id, statusReason, adminUserId, baseUrl);
                    songMissing = !songDisabled;
                    creatorAlreadyNotified = songDisabled;
                }
                else if (!accepted && isReversal && !song.IsEnabled)
                {
                    // A song can be held down by more than one finding. Re-enabling on "it is
                    // disabled and this appeal won" alone would republish a song that a separate,
                    // still-upheld report says must stay off - a different claim, judged on its own.
                    heldDownByAnotherFinding = await HasOtherUpheldFindingAsync(context, report, siblingIds);

                    if (!heldDownByAnotherFinding)
                    {
                        songReEnabled = await _songStatusService.EnableSongAsync(song.Id, statusReason, adminUserId, baseUrl);
                        songMissing = !songReEnabled;
                        creatorAlreadyNotified = songReEnabled;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not change the status of song {SongMetadataId} while deciding report {ReportId}", song.Id, report.Id);
                await ReleaseClaimAsync(context, report.Id, siblings, previousDecision, previousDecidedAt, isReversal);
                return new ReportResolutionResult(false, false,
                    "The song's status could not be changed, so the report was left as it was. Please try again.");
            }
        }

        // Reports under the *other* reason are a different claim and are deliberately left alone -
        // dismissing a terms-of-use complaint says nothing about a copyright one. The admin is told
        // rather than left to notice the row still sitting there.
        var otherReasonsStillOpen = await context.ReportedSongs
            .CountAsync(r => r.SongMetadataId == report.SongMetadataId
                          && r.Reason != report.Reason
                          && r.ResolutionDateTime == null);

        await SendDecisionEmailsAsync(report, songTitle, accepted, adminReason, isReversal,
            songDisabled, songReEnabled, creatorAlreadyNotified, heldDownByAnotherFinding);

        return new ReportResolutionResult(
            true,
            songDisabled,
            BuildAdminMessage(accepted, isReversal, songMissing, songDisabled, songReEnabled,
                heldDownByAnotherFinding, claimedSiblings, otherReasonsStillOpen));
    }

    /// <summary>
    /// Stamps the decision onto rows that are still in the state this decision was made against,
    /// and returns how many that was. Pinning the old state in the WHERE clause is what makes the
    /// claim atomic - the database applies it to rows nobody else can be writing at that instant.
    /// </summary>
    private static async Task<int> ClaimAsync(
        IQueryable<ReportedSong> rows,
        bool? previousDecision,
        bool isReversal,
        bool accepted,
        DateTime decidedAt)
    {
        var pinned = isReversal
            ? rows.Where(r => r.ResolutionAccepted == previousDecision)
            : rows.Where(r => r.ResolutionDateTime == null);

        return await pinned.ExecuteUpdateAsync(setters => setters
            .SetProperty(r => r.ResolutionAccepted, accepted)
            .SetProperty(r => r.ResolutionDateTime, (DateTime?)decidedAt));
    }

    /// <summary>
    /// Puts the claim back when the song could not be changed, so a retry starts from where the
    /// admin left off rather than from a decision that was recorded but never carried out.
    /// </summary>
    private async Task ReleaseClaimAsync(
        AppDbContext context,
        int reportId,
        List<ReportedSong> siblings,
        bool? previousDecision,
        DateTime? previousDecidedAt,
        bool isReversal)
    {
        try
        {
            await context.ReportedSongs
                .Where(r => r.Id == reportId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.ResolutionAccepted, previousDecision)
                    .SetProperty(r => r.ResolutionDateTime, previousDecidedAt));

            // Each sibling carried its own decision timestamp, so they are restored one at a time
            // rather than all stamped with the target's.
            foreach (var sibling in siblings)
            {
                var siblingDecision = isReversal ? sibling.ResolutionAccepted : null;
                var siblingDecidedAt = isReversal ? sibling.ResolutionDateTime : null;

                await context.ReportedSongs
                    .Where(r => r.Id == sibling.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(r => r.ResolutionAccepted, siblingDecision)
                        .SetProperty(r => r.ResolutionDateTime, siblingDecidedAt));
            }
        }
        catch (Exception ex)
        {
            // Nothing else can be done from here, but it must not be silent: the report now reads
            // as decided while the song was never touched, and only the log says so.
            _logger.LogError(ex, "Could not release the claim on report {ReportId} after a failed status change", reportId);
        }
    }

    /// <summary>
    /// Whether some other upheld report is still keeping this song down. Reports in the group being
    /// decided are excluded - they are the ones whose finding is being lifted right now.
    /// </summary>
    private static async Task<bool> HasOtherUpheldFindingAsync(
        AppDbContext context,
        ReportedSong report,
        List<int> siblingIds)
    {
        return await context.ReportedSongs
            .AnyAsync(r => r.SongMetadataId == report.SongMetadataId
                        && r.Id != report.Id
                        && !siblingIds.Contains(r.Id)
                        && r.ResolutionAccepted == true);
    }

    /// <summary>
    /// The other reports this decision also settles: same song, same reason. Deciding sweeps up
    /// everything still open; reversing sweeps up everything still holding the decision being
    /// reversed, so the group never splits into two different verdicts on the same complaint.
    /// </summary>
    private static async Task<List<ReportedSong>> FindSiblingReportsAsync(
        AppDbContext context,
        ReportedSong report,
        bool? previousDecision,
        bool isReversal)
    {
        var query = context.ReportedSongs
            .AsNoTracking()
            .Where(r => r.Id != report.Id
                     && r.SongMetadataId == report.SongMetadataId
                     && r.Reason == report.Reason);

        query = isReversal
            ? query.Where(r => r.ResolutionAccepted == previousDecision)
            : query.Where(r => r.ResolutionDateTime == null);

        return await query.ToListAsync();
    }

    /// <summary>SongStatusHistory.Reason and SongMetadata.StatusReason are both MaxLength(1000).</summary>
    private const int StatusReasonMaxLength = 1000;

    private static string BuildStatusReason(string reportReason, bool accepted, string? adminReason, bool isReversal)
    {
        if (!isReversal)
            return $"A listener report for {reportReason} was reviewed and upheld.";

        var outcome = accepted
            ? $"An earlier decision to dismiss a listener report for {reportReason} was reversed, and the report is now upheld."
            : $"An appeal succeeded. The listener report for {reportReason} is no longer upheld.";

        var composed = string.IsNullOrWhiteSpace(adminReason) ? outcome : $"{outcome} {adminReason}";

        // The admin's reason is free text and this string is written to two columns capped at 1000.
        // Over the cap the status service throws, which surfaces as "could not be changed, please
        // try again" - advice that would fail the same way on every retry. The UI caps the input
        // too; this is the backstop, since the sentence around it grows whenever the copy changes.
        return composed.Length <= StatusReasonMaxLength
            ? composed
            : composed[..(StatusReasonMaxLength - 3)] + "...";
    }

    private static string BuildAdminMessage(
        bool accepted,
        bool isReversal,
        bool songMissing,
        bool songDisabled,
        bool songReEnabled,
        bool heldDownByAnotherFinding,
        int siblingCount,
        int otherReasonsStillOpen)
    {
        var decision = accepted ? "upheld" : "dismissed";
        var prefix = isReversal ? $"Decision reversed - the report is now {decision}." : $"Report {decision}.";

        var outcome = songDisabled
            ? "The song has been disabled and removed from every playlist, and the creator has been emailed."
            : songReEnabled
                ? "The song is available again, and the creator has been emailed."
                : heldDownByAnotherFinding
                    ? "The song stays unavailable: another upheld report about it is still standing, and has to be reversed too before the song can come back. The creator has been emailed."
                    : songMissing
                        ? "The song no longer exists, so nothing was changed."
                        : accepted
                            ? "The song was already disabled, so it was left as it is. The creator has been emailed."
                            : "The song was left as it is, and the creator has been emailed.";

        var siblingNote = siblingCount switch
        {
            0 => string.Empty,
            1 => $" One other report of the same thing about this song was {decision} with it.",
            _ => $" {siblingCount} other reports of the same thing about this song were {decision} with it."
        };

        var otherNote = otherReasonsStillOpen switch
        {
            0 => string.Empty,
            1 => " One report of something else about this song is still open and needs its own decision.",
            _ => $" {otherReasonsStillOpen} reports of something else about this song are still open and need their own decisions."
        };

        return $"{prefix} {outcome}{siblingNote}{otherNote}";
    }

    /// <summary>
    /// Tells the admin and the creator what was decided. The reporter is deliberately not told the
    /// outcome: they already have the receipt from reporting, and naming the result back to them
    /// turns a report into a channel for confirming that a particular creator was punished.
    /// </summary>
    private async Task SendDecisionEmailsAsync(
        ReportedSong report,
        string songTitle,
        bool accepted,
        string? adminReason,
        bool isReversal,
        bool songDisabled,
        bool songReEnabled,
        bool creatorAlreadyNotified,
        bool heldDownByAnotherFinding)
    {
        var encodedTitle = System.Web.HttpUtility.HtmlEncode(songTitle);
        var encodedReason = System.Web.HttpUtility.HtmlEncode(report.Reason);
        var reporterEmail = report.ReportingUser?.Email ?? "Unknown";

        var decision = accepted ? "upheld" : "dismissed";
        var headline = isReversal
            ? $"Report Decision Reversed - Now {(accepted ? "Upheld" : "Dismissed")}"
            : $"Report {(accepted ? "Upheld" : "Dismissed")}";

        var songLine = songDisabled
            ? "The song has been disabled and removed from all playlists."
            : songReEnabled
                ? "The song has been re-enabled and is available again."
                : heldDownByAnotherFinding
                    ? "The song stays disabled because another upheld report about it is still standing."
                    : "The song's status was not changed.";

        var adminNote = string.IsNullOrWhiteSpace(adminReason)
            ? string.Empty
            : $" Reason given: {System.Web.HttpUtility.HtmlEncode(adminReason)}";

        await SendEmailSafeAsync(
            AdminNotificationService.AdminEmail,
            $"StreamTunes Admin - Report {(accepted ? "Upheld" : "Dismissed")}: {songTitle}",
            BuildEmailBody(headline,
                $"A reported song has been reviewed and the report was {decision}. {songLine}{adminNote}",
                encodedTitle, encodedReason, reporterEmail));

        // The creator hears about a status change from SongStatusService, which says far more about
        // what it means for them than this could. This mail is for the cases where nothing moved.
        if (creatorAlreadyNotified)
            return;

        var creatorEmail = report.SongMetadata?.Creator?.User?.Email;
        if (string.IsNullOrEmpty(creatorEmail))
            return;

        var creatorMessage = accepted
            ? $"A report about one of your songs has been reviewed and upheld.{adminNote}"
            : isReversal
                ? heldDownByAnotherFinding
                    ? $"Your appeal succeeded and this report is no longer upheld. Your song stays unavailable for now, because a separate report about it is still upheld.{adminNote}"
                    : $"Your appeal succeeded. The report about one of your songs is no longer upheld.{adminNote}"
                : "A report about one of your songs has been reviewed and dismissed. No action has been taken and your song is unaffected.";

        await SendEmailSafeAsync(
            creatorEmail,
            $"StreamTunes - Report {(accepted ? "Upheld" : "Dismissed")}: {songTitle}",
            BuildEmailBody(headline, creatorMessage, encodedTitle, encodedReason, null));
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
