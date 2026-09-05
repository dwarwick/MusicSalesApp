#nullable enable
using System.Web;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class ArtistReleaseNotificationService : IArtistReleaseNotificationService
{
    /// <summary>
    /// How far back a release stays notifiable.
    /// </summary>
    /// <remarks>
    /// The window exists so that a job outage, or this feature being deployed onto an existing
    /// catalogue, cannot produce a flood of notifications about old music. Combined with the
    /// "followed before it was published" rule below it means the worst case after a week-long
    /// outage is one round of slightly late notifications, not a retrospective of the archive.
    /// </remarks>
    private static readonly TimeSpan NotifiableWindow = TimeSpan.FromDays(7);

    private const int EmailBatchSize = 10;
    private const int DelayBetweenEmailsMs = 5000;
    private const int DelayBetweenBatchesMs = 60000;

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IEmailService _emailService;
    private readonly ILogger<ArtistReleaseNotificationService> _logger;

    public ArtistReleaseNotificationService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IEmailService emailService,
        ILogger<ArtistReleaseNotificationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _emailService = emailService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> CreatePendingNotificationsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var now = DateTime.UtcNow;

        // Step 1: stamp anything that has become publicly visible since the last run.
        //
        // This happens whether or not the persona is currently available, and that is deliberate:
        // a song uploaded while its persona is disabled is still "published" the moment it goes
        // live. Not stamping it would leave it eligible forever, so re-enabling a persona months
        // later would notify every follower about a back catalogue all at once.
        var stamped = await context.SongMetadata
            .WherePubliclyReleased()
            .Where(song => song.FirstPublishedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(song => song.FirstPublishedAtUtc, now));

        if (stamped > 0)
        {
            _logger.LogInformation("Stamped {Count} songs as first published.", stamped);
        }

        // Step 2: notify. Only songs still inside the window, and only from artists a listener
        // could actually be hearing from right now.
        var since = now - NotifiableWindow;

        var releases = await context.SongMetadata
            .AsNoTracking()
            .WherePubliclyReleased()
            .Where(song => song.FirstPublishedAtUtc != null && song.FirstPublishedAtUtc >= since)
            .Where(song => context.CreatorPersonas
                .WherePubliclyActive()
                .Any(persona => persona.Id == song.PersonaId))
            .Select(song => new
            {
                SongMetadataId = song.Id,
                PersonaId = song.PersonaId!.Value,
                PublishedAtUtc = song.FirstPublishedAtUtc!.Value,
            })
            .ToListAsync();

        if (releases.Count == 0)
        {
            return 0;
        }

        var created = 0;

        foreach (var release in releases)
        {
            // "Followed before it was published" is the rule that stops a new follower being sent
            // a notification about music that was already out when they arrived. It is also what
            // makes deploying this feature onto a live catalogue safe: on day one no follow row
            // predates any release, so nothing is sent.
            var recipients = await context.ArtistFollowers
                .AsNoTracking()
                .WhereActiveFollow()
                .Where(follow => follow.CreatorPersonaId == release.PersonaId
                                 && follow.ReleaseNotificationsEnabled
                                 && follow.FollowedDateUtc <= release.PublishedAtUtc)
                .Select(follow => follow.ListenerUserId)
                .ToListAsync();

            if (recipients.Count == 0)
            {
                continue;
            }

            var alreadyNotified = await context.ArtistReleaseNotifications
                .AsNoTracking()
                .Where(notification => notification.SongMetadataId == release.SongMetadataId
                                       && recipients.Contains(notification.ListenerUserId))
                .Select(notification => notification.ListenerUserId)
                .ToListAsync();

            var pending = recipients.Except(alreadyNotified).ToList();
            if (pending.Count == 0)
            {
                continue;
            }

            context.ArtistReleaseNotifications.AddRange(pending.Select(listenerUserId =>
                new ArtistReleaseNotification
                {
                    CreatorPersonaId = release.PersonaId,
                    SongMetadataId = release.SongMetadataId,
                    ListenerUserId = listenerUserId,
                    CreatedDateUtc = now,
                }));

            try
            {
                await context.SaveChangesAsync();
                created += pending.Count;
            }
            catch (DbUpdateException ex)
            {
                // The unique index on (SongMetadataId, ListenerUserId) refused a duplicate, which
                // means a concurrent run beat us to some of these. Nothing is lost - the rows the
                // other run wrote are the same rows - so drop this batch and carry on.
                _logger.LogWarning(
                    ex,
                    "Release notifications for song {SongMetadataId} collided with a concurrent run.",
                    release.SongMetadataId);

                foreach (var entry in context.ChangeTracker.Entries<ArtistReleaseNotification>().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        if (created > 0)
        {
            _logger.LogInformation("Created {Count} artist release notifications.", created);
        }

        return created;
    }

    /// <inheritdoc />
    public async Task<int> SendPendingEmailsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var pending = await context.ArtistReleaseNotifications
            .Where(notification => notification.EmailSentDateUtc == null)
            .OrderBy(notification => notification.CreatedDateUtc)
            .Select(notification => new PendingReleaseEmail
            {
                NotificationId = notification.Id,
                ArtistName = notification.CreatorPersona.Name,
                SongTitle = notification.SongMetadata.SongTitle,
                Mp3BlobPath = notification.SongMetadata.Mp3BlobPath,
                BlobPath = notification.SongMetadata.BlobPath,
                SongIsAvailable = notification.SongMetadata.IsActive && notification.SongMetadata.IsEnabled,
                RecipientEmail = notification.ListenerUser.Email,
                WantsEmail = notification.ListenerUser.ReceiveArtistReleaseEmails,
                EmailConfirmed = notification.ListenerUser.EmailConfirmed,
                IsSuspended = notification.ListenerUser.IsSuspended,
            })
            .ToListAsync();

        if (pending.Count == 0)
        {
            return 0;
        }

        // Stamp the ones that must never be emailed so the job stops reconsidering them nightly.
        // Their in-app notification is untouched - the row itself is that.
        var skipped = pending.Where(item => !item.ShouldSend).Select(item => item.NotificationId).ToList();

        if (skipped.Count > 0)
        {
            await context.ArtistReleaseNotifications
                .Where(notification => skipped.Contains(notification.Id))
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(notification => notification.EmailSentDateUtc, DateTime.UtcNow));

            _logger.LogInformation(
                "Skipped {Count} release notification emails (opted out, unconfirmed, suspended or song withdrawn).",
                skipped.Count);
        }

        var sendable = pending.Where(item => item.ShouldSend).ToList();
        if (sendable.Count == 0)
        {
            return 0;
        }

        var baseUrl = _emailService.GetAppBaseUrl();
        var sent = 0;

        for (var index = 0; index < sendable.Count; index++)
        {
            var item = sendable[index];

            try
            {
                var delivered = await _emailService.SendEmailAsync(
                    item.RecipientEmail!,
                    $"New music from {item.ArtistName}",
                    BuildReleaseEmailBody(item, baseUrl));

                if (delivered)
                {
                    sent++;
                }
                else
                {
                    _logger.LogWarning("Failed to email release notification {NotificationId}.", item.NotificationId);
                }

                // Stamped either way: a permanently undeliverable address would otherwise be
                // retried every night forever, and the listener has the notification in-app.
                await context.ArtistReleaseNotifications
                    .Where(notification => notification.Id == item.NotificationId)
                    .ExecuteUpdateAsync(setters =>
                        setters.SetProperty(notification => notification.EmailSentDateUtc, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error emailing release notification {NotificationId}.", item.NotificationId);
            }

            if (index == sendable.Count - 1)
            {
                break;
            }

            await Task.Delay((index + 1) % EmailBatchSize == 0 ? DelayBetweenBatchesMs : DelayBetweenEmailsMs);
        }

        _logger.LogInformation("Sent {Sent} of {Total} release notification emails.", sent, sendable.Count);
        return sent;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtistReleaseNotificationDto>> GetForListenerAsync(
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await context.ArtistReleaseNotifications
            .AsNoTracking()
            .Where(notification => notification.ListenerUserId == listenerUserId)
            .OrderByDescending(notification => notification.CreatedDateUtc)
            .Select(notification => new
            {
                notification.Id,
                notification.CreatorPersonaId,
                ArtistName = notification.CreatorPersona.Name,
                notification.SongMetadataId,
                notification.SongMetadata.SongTitle,
                notification.SongMetadata.Mp3BlobPath,
                notification.SongMetadata.BlobPath,
                // A song can be pulled after the notification went out. The row stays as history
                // and says so, rather than linking the listener to a page that will not load.
                SongIsAvailable = notification.SongMetadata.IsActive && notification.SongMetadata.IsEnabled,
                notification.CreatedDateUtc,
                notification.ReadDateUtc,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ArtistReleaseNotificationDto(
            row.Id,
            row.CreatorPersonaId,
            string.IsNullOrWhiteSpace(row.ArtistName) ? ArtistDisplayNames.UnknownArtist : row.ArtistName,
            row.SongMetadataId,
            SongTitleHelper.GetEffectiveTitle(row.SongTitle, row.Mp3BlobPath, row.BlobPath),
            row.SongIsAvailable,
            row.CreatedDateUtc,
            row.ReadDateUtc is not null)).ToList();
    }

    /// <inheritdoc />
    public async Task<int> GetUnreadCountAsync(int listenerUserId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await context.ArtistReleaseNotifications
            .CountAsync(
                notification => notification.ListenerUserId == listenerUserId && notification.ReadDateUtc == null,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> MarkReadAsync(
        int notificationId,
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var notification = await context.ArtistReleaseNotifications
            .FirstOrDefaultAsync(
                row => row.Id == notificationId && row.ListenerUserId == listenerUserId,
                cancellationToken);

        if (notification is null)
        {
            return false;
        }

        notification.ReadDateUtc ??= DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private string BuildReleaseEmailBody(PendingReleaseEmail item, string baseUrl)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var artist = HttpUtility.HtmlEncode(item.ArtistName);
        var title = SongTitleHelper.GetEffectiveTitle(item.SongTitle, item.Mp3BlobPath, item.BlobPath);
        var encodedTitle = HttpUtility.HtmlEncode(title);
        var songUrl = $"{trimmedBase}/song/{Uri.EscapeDataString(title ?? string.Empty)}";
        var artistUrl = $"{trimmedBase}/artist/{Uri.EscapeDataString(item.ArtistName ?? string.Empty)}";
        var preferencesUrl = $"{trimmedBase}{AppPageRoutes.ManageAccount}";

        return $"""
                <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
                    <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                        {_emailService.GetEmailLogoHtml()}
                        <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>New music from {artist}</h1>
                    </div>
                    <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                        <p style='font-size: 16px; color: #333;'><a href='{artistUrl}' style='color: #1a1a2e;'>{artist}</a> just released <strong>{encodedTitle}</strong>.</p>
                        <div style='text-align: center; margin: 30px 0;'>
                            <a href='{songUrl}' style='display: inline-block; padding: 15px 30px; background-color: #1a1a2e; color: white; text-decoration: none; border-radius: 5px; font-size: 16px;'>Listen Now</a>
                        </div>
                        <p style='color: #666; font-size: 14px;'>You are getting this because you follow {artist} on StreamTunes.</p>
                        <div style='margin-top: 30px; padding-top: 20px; border-top: 1px solid #e0e0e0; text-align: center;'>
                            <p style='color: #999; font-size: 12px;'>
                                <a href='{preferencesUrl}' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
                            </p>
                        </div>
                    </div>
                </div>
                """;
    }

    /// <summary>
    /// One candidate email, flattened out of the query so the send loop holds no entities and no
    /// open reader while it sleeps between messages.
    /// </summary>
    private sealed class PendingReleaseEmail
    {
        public int NotificationId { get; init; }
        public string? ArtistName { get; init; }
        public string? SongTitle { get; init; }
        public string? Mp3BlobPath { get; init; }
        public string? BlobPath { get; init; }
        public bool SongIsAvailable { get; init; }
        public string? RecipientEmail { get; init; }
        public bool WantsEmail { get; init; }
        public bool EmailConfirmed { get; init; }
        public bool IsSuspended { get; init; }

        public bool ShouldSend =>
            WantsEmail
            && EmailConfirmed
            && !IsSuspended
            && SongIsAvailable
            && !string.IsNullOrWhiteSpace(RecipientEmail);
    }
}
