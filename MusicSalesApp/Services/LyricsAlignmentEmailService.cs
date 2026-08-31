#nullable enable
using System.Net;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>Tells a creator their lyric timing has finished, one way or the other.</summary>
public interface ILyricsAlignmentEmailService
{
    /// <summary>
    /// Send the completion email for one attempt. Safe to call for an attempt that no longer exists.
    /// </summary>
    // Hangfire resolves filters from Job.Method, which for an interface-registered job is
    // this declaration. The same attribute on the implementation is silently ignored.
    [AutomaticRetry(Attempts = 3)]
    Task SendCompletionEmailAsync(Guid jobId);
}

/// <summary>
/// The email a creator gets when their lyric timing lands.
///
/// <para>
/// <b>Enqueued through Hangfire, never sent inline, and that is not a preference.</b> Both terminal
/// paths run on the Function's callback request, whose contract allows two minutes and whose own
/// documentation warns that overrunning it makes the Function abandon the request and retry assembly
/// on top of one still in flight. <c>IEmailService.SendEmailAsync</c> is synchronous SMTP with a
/// 30-second timeout. Putting that in the callback path trades a working alignment for a mail server
/// having a bad afternoon.
/// </para>
///
/// <para>
/// It also buys retries for free, which matters because this is the only notification a creator gets
/// if they closed the tab - the SignalR progress bar reaches nobody once the circuit is gone, and
/// timing takes minutes, so closing the tab is the expected behaviour rather than the exception.
/// </para>
/// </summary>
public sealed class LyricsAlignmentEmailService : ILyricsAlignmentEmailService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ICreatorService _creatorService;
    private readonly IEmailService _emailService;
    private readonly IAppSettingsService _appSettings;
    private readonly ILogger<LyricsAlignmentEmailService> _logger;

    public LyricsAlignmentEmailService(
        IDbContextFactory<AppDbContext> contextFactory,
        ICreatorService creatorService,
        IEmailService emailService,
        IAppSettingsService appSettings,
        ILogger<LyricsAlignmentEmailService> logger)
    {
        _contextFactory = contextFactory;
        _creatorService = creatorService;
        _emailService = emailService;
        _appSettings = appSettings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendCompletionEmailAsync(Guid jobId)
    {
        // Checked inside the job rather than before enqueuing, so switching it off drains whatever is
        // already queued instead of letting a backlog arrive after the decision to stop.
        if (!await _appSettings.GetLyricsCompletionEmailsEnabledAsync())
        {
            _logger.LogInformation(
                "Lyrics completion emails are switched off; skipping the one for {JobId}.", jobId);
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync();

        // Loaded here with the song included, which is also how the failure path gets a song title -
        // LyricsAlignmentCompletionService.FailAsync deliberately loads the job with no includes to
        // keep the callback cheap, so asking for it there would slow down every failure to decorate
        // an email.
        var job = await context.LyricsAlignmentJobs
            .AsNoTracking()
            .Include(row => row.SongMetadata)
            .FirstOrDefaultAsync(row => row.JobId == jobId);

        if (job is null)
        {
            _logger.LogInformation("Lyrics alignment {JobId} no longer exists; no email to send.", jobId);
            return;
        }

        var creator = await _creatorService.GetCreatorByIdAsync(job.CreatorId);
        var email = creator?.User?.Email;

        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogWarning(
                "No email address for creator {CreatorId}; cannot report lyrics job {JobId}.",
                job.CreatorId,
                jobId);
            return;
        }

        if (creator!.User!.EmailConfirmed != true)
        {
            _logger.LogInformation(
                "Creator {CreatorId} has not confirmed their email; not sending the lyrics result.",
                job.CreatorId);
            return;
        }

        var title = job.SongMetadata is null
            ? "your song"
            : SongTitleHelper.GetEffectiveTitle(
                job.SongMetadata.SongTitle,
                job.SongMetadata.Mp3BlobPath,
                job.SongMetadata.BlobPath);

        var succeeded = job.Status == LyricsAlignmentJobStatus.Completed;

        var subject = succeeded
            ? $"StreamTunes - \"{title}\" lyrics are timed and ready for you to check"
            : $"StreamTunes - we couldn't time the lyrics for \"{title}\"";

        var body = succeeded
            ? BuildSuccessBody(title, job.SongMetadataId)
            : BuildFailureBody(title, job.SongMetadataId, job.FailureMessage);

        // Deliberately not caught. Hangfire retries this three times, and an SMTP server that is
        // briefly unreachable is exactly what those retries are for; swallowing the exception here
        // would turn a transient fault into a creator who is never told.
        await _emailService.SendEmailAsync(email!, subject, body);

        _logger.LogInformation(
            "Told creator {CreatorId} that lyrics job {JobId} finished ({Outcome}).",
            job.CreatorId,
            jobId,
            succeeded ? "completed" : "failed");
    }

    // No confidence figure. The aligner's score reads far worse than the timings it describes, and an
    // email that leads with "52% confidence" is the last thing a creator sees before they decide
    // whether the result is worth listening to.
    private string BuildSuccessBody(string title, int songMetadataId)
    {
        var editorUrl = EditorUrl(songMetadataId);
        var safeTitle = WebUtility.HtmlEncode(title);

        return $@"
            {_emailService.GetEmailLogoHtml()}
            <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; color: #222;'>
                <h2 style='margin: 0 0 16px 0;'>The lyrics for &ldquo;{safeTitle}&rdquo; are timed</h2>

                <p style='margin: 0 0 16px 0;'>
                    We've worked out when each word is sung, so the lyrics can scroll and light up in
                    time with your song.
                </p>

                <p style='margin: 0 0 16px 0; padding: 12px; background: #fff8e1; border-left: 4px solid #f0ad4e;'>
                    <strong>These aren't live yet.</strong> Have a listen first &mdash; if anything is
                    out of step you can fix it, and nothing is shown to listeners until you press
                    Publish.
                </p>

                <p style='margin: 0 0 24px 0;'>
                    <a href='{editorUrl}'
                       style='background: #1db954; color: #fff; padding: 12px 24px; text-decoration: none;
                              border-radius: 4px; display: inline-block;'>
                        Preview lyrics
                    </a>
                </p>

                {FooterHtml()}
            </div>";
    }

    private string BuildFailureBody(string title, int songMetadataId, string? failureMessage)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var reason = string.IsNullOrWhiteSpace(failureMessage)
            ? "Something went wrong while we were working on them."
            : WebUtility.HtmlEncode(failureMessage);

        return $@"
            {_emailService.GetEmailLogoHtml()}
            <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; color: #222;'>
                <h2 style='margin: 0 0 16px 0;'>We couldn't time the lyrics for &ldquo;{safeTitle}&rdquo;</h2>

                <p style='margin: 0 0 16px 0;'>{reason}</p>

                <p style='margin: 0 0 16px 0;'>
                    Nothing has changed for your listeners, and the lyrics you pasted are still saved.
                    It is often worth trying again after removing anything that isn't actually sung
                    &mdash; spoken asides, credits, or notes to yourself.
                </p>

                <p style='margin: 0 0 24px 0;'>
                    <a href='{EditorUrl(songMetadataId)}'
                       style='background: #444; color: #fff; padding: 12px 24px; text-decoration: none;
                              border-radius: 4px; display: inline-block;'>
                        Open my songs
                    </a>
                </p>

                {FooterHtml()}
            </div>";
    }

    private string EditorUrl(int songMetadataId) =>
        $"{_emailService.GetAppBaseUrl().TrimEnd('/')}{AppPageRoutes.CreatorSongLyrics(songMetadataId)}";

    private string FooterHtml() =>
        $@"<hr style='border: none; border-top: 1px solid #ddd; margin: 24px 0;' />
           <p style='font-size: 12px; color: #777; margin: 0;'>
               You're getting this because you asked us to time the lyrics for one of your songs.
               <a href='{_emailService.GetAppBaseUrl().TrimEnd('/')}{AppPageRoutes.ManageAccount}'
                  style='color: #777;'>Manage your email preferences</a>.
           </p>";
}
