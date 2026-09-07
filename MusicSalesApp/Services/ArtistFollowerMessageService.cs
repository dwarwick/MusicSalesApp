#nullable enable
using System.Web;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class ArtistFollowerMessageService : IArtistFollowerMessageService
{
    /// <summary>
    /// Thank-yous one persona may send in a rolling 24 hours.
    /// </summary>
    /// <remarks>
    /// The per-relationship limit of one is the real anti-spam control; this is the backstop that
    /// stops a scripted client working through a large follower list in a single burst. It is set
    /// well above what a person sends by hand, so a creator thanking their followers over an
    /// evening never meets it.
    /// </remarks>
    private const int DailyThankYouLimitPerPersona = 100;

    // Same shape as NewSongNotificationService: individual sends, spaced out, in small batches.
    // Spam filters judge a sender's whole reputation, so one careless loop here degrades delivery
    // for the verification and password-reset mail too.
    private const int EmailBatchSize = 10;
    private const int DelayBetweenEmailsMs = 5000;
    private const int DelayBetweenBatchesMs = 60000;

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IEmailService _emailService;
    private readonly ILogger<ArtistFollowerMessageService> _logger;

    public ArtistFollowerMessageService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IEmailService emailService,
        ILogger<ArtistFollowerMessageService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _emailService = emailService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ArtistThankYouResult> SendThankYouAsync(
        int artistFollowerId,
        int creatorId,
        string messageText,
        int? relatedSongMetadataId = null,
        CancellationToken cancellationToken = default)
    {
        // Content first. It costs nothing, and a creator fixing their wording should not have to
        // wait on database round trips to be told what is wrong with it.
        if (!ArtistMessageContentPolicy.TryValidate(messageText, out var normalized, out var rejectionReason))
        {
            return new ArtistThankYouResult(ArtistThankYouOutcome.ContentRejected, rejectionReason);
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var follow = await context.ArtistFollowers
            .Include(row => row.CreatorPersona)
            .FirstOrDefaultAsync(row => row.Id == artistFollowerId, cancellationToken);

        if (follow?.CreatorPersona is null || follow.CreatorPersona.CreatorId != creatorId)
        {
            // Covers both "no such follower" and "someone else's follower", and deliberately does
            // not distinguish them: telling a caller that a follower id exists but belongs to
            // another artist is itself a small leak.
            return new ArtistThankYouResult(ArtistThankYouOutcome.NotPersonaOwner);
        }

        var personaIsAvailable = await context.CreatorPersonas
            .WherePubliclyActive()
            .AnyAsync(persona => persona.Id == follow.CreatorPersonaId, cancellationToken);

        if (!personaIsAvailable)
        {
            return new ArtistThankYouResult(ArtistThankYouOutcome.ArtistUnavailable);
        }

        if (follow.IsBlockedByListener || !follow.ArtistMessagesEnabled)
        {
            return new ArtistThankYouResult(ArtistThankYouOutcome.Blocked);
        }

        if (!follow.IsActive)
        {
            // Unfollowing ends the creator's ability to start anything new. Existing messages stay
            // in the listener's history; this is only about initiating.
            return new ArtistThankYouResult(ArtistThankYouOutcome.NotFollowing);
        }

        var alreadyThanked = await context.ArtistFollowerMessages
            .AnyAsync(
                message => message.ArtistFollowerId == artistFollowerId
                           && message.MessageKind == ArtistMessageKinds.ThankYou,
                cancellationToken);

        if (alreadyThanked)
        {
            return new ArtistThankYouResult(ArtistThankYouOutcome.AlreadyThanked);
        }

        if (await CountThankYousInLastDayAsync(context, follow.CreatorPersonaId, cancellationToken)
            >= DailyThankYouLimitPerPersona)
        {
            return new ArtistThankYouResult(ArtistThankYouOutcome.RateLimited);
        }

        var senderUserId = await context.Creators
            .Where(creator => creator.Id == creatorId)
            .Select(creator => creator.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (senderUserId == 0)
        {
            return new ArtistThankYouResult(ArtistThankYouOutcome.NotPersonaOwner);
        }

        var relatedSongId = await ResolveRelatedSongAsync(
            context, follow.CreatorPersonaId, relatedSongMetadataId, cancellationToken);

        context.ArtistFollowerMessages.Add(new ArtistFollowerMessage
        {
            ArtistFollowerId = artistFollowerId,
            SenderUserId = senderUserId,
            MessageKind = ArtistMessageKinds.ThankYou,
            MessageText = normalized,
            CreatedDateUtc = DateTime.UtcNow,
            RelatedSongMetadataId = relatedSongId,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // The filtered unique index caught a double submit. It is the authority on
            // one-per-follower; the check above is only there to give a clean answer first.
            _logger.LogDebug(
                ex,
                "Duplicate thank-you rejected by the unique index for follower {ArtistFollowerId}.",
                artistFollowerId);

            return new ArtistThankYouResult(ArtistThankYouOutcome.AlreadyThanked);
        }

        _logger.LogInformation(
            "Creator {CreatorId} thanked follower {ArtistFollowerId} of persona {PersonaId}.",
            creatorId,
            artistFollowerId,
            follow.CreatorPersonaId);

        return new ArtistThankYouResult(ArtistThankYouOutcome.Sent);
    }

    /// <inheritdoc />
    public async Task<int> GetRemainingDailyThankYousAsync(
        int creatorPersonaId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var used = await CountThankYousInLastDayAsync(context, creatorPersonaId, cancellationToken);
        return Math.Max(0, DailyThankYouLimitPerPersona - used);
    }

    private static Task<int> CountThankYousInLastDayAsync(
        AppDbContext context,
        int creatorPersonaId,
        CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-1);

        return context.ArtistFollowerMessages
            .CountAsync(
                message => message.CreatedDateUtc >= since
                           && message.MessageKind == ArtistMessageKinds.ThankYou
                           && message.ArtistFollower.CreatorPersonaId == creatorPersonaId,
                cancellationToken);
    }

    /// <summary>
    /// Only lets a message point at a song the persona actually owns, so the "about this song"
    /// line on a listener's message cannot be made to name someone else's track.
    /// </summary>
    private static async Task<int?> ResolveRelatedSongAsync(
        AppDbContext context,
        int creatorPersonaId,
        int? relatedSongMetadataId,
        CancellationToken cancellationToken)
    {
        if (relatedSongMetadataId is not > 0)
        {
            return null;
        }

        var belongsToPersona = await context.SongMetadata
            .AnyAsync(
                song => song.Id == relatedSongMetadataId && song.PersonaId == creatorPersonaId,
                cancellationToken);

        return belongsToPersona ? relatedSongMetadataId : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtistMessageDto>> GetMessagesForListenerAsync(
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Note what is NOT selected: SenderUserId and everything reachable through it. A listener
        // learns which persona wrote to them and nothing about the account behind it.
        var rows = await context.ArtistFollowerMessages
            .AsNoTracking()
            .Where(message => message.ArtistFollower.ListenerUserId == listenerUserId
                              && !message.IsHiddenByListener)
            .OrderByDescending(message => message.CreatedDateUtc)
            .Select(message => new
            {
                message.Id,
                message.ArtistFollower.CreatorPersonaId,
                ArtistName = message.ArtistFollower.CreatorPersona.Name,
                message.MessageText,
                message.RelatedSongMetadataId,
                RelatedSongTitle = message.RelatedSongMetadata == null ? null : message.RelatedSongMetadata.SongTitle,
                RelatedSongMp3BlobPath = message.RelatedSongMetadata == null ? null : message.RelatedSongMetadata.Mp3BlobPath,
                RelatedSongBlobPath = message.RelatedSongMetadata == null ? null : message.RelatedSongMetadata.BlobPath,
                message.CreatedDateUtc,
                message.ReadDateUtc,
                message.IsReported,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ArtistMessageDto(
            row.Id,
            row.CreatorPersonaId,
            string.IsNullOrWhiteSpace(row.ArtistName) ? ArtistDisplayNames.UnknownArtist : row.ArtistName,
            row.MessageText,
            row.RelatedSongMetadataId,
            row.RelatedSongMetadataId is null
                ? null
                : SongTitleHelper.GetEffectiveTitle(
                    row.RelatedSongTitle,
                    row.RelatedSongMp3BlobPath,
                    row.RelatedSongBlobPath),
            row.CreatedDateUtc,
            row.ReadDateUtc is not null,
            row.IsReported)).ToList();
    }

    /// <inheritdoc />
    public async Task<int> GetUnreadMessageCountAsync(
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await context.ArtistFollowerMessages
            .CountAsync(
                message => message.ArtistFollower.ListenerUserId == listenerUserId
                           && message.ReadDateUtc == null
                           && !message.IsHiddenByListener,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> MarkReadAsync(
        int messageId,
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var message = await LoadOwnedMessageAsync(context, messageId, listenerUserId, cancellationToken);
        if (message is null)
        {
            return false;
        }

        message.ReadDateUtc ??= DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> HideAsync(
        int messageId,
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var message = await LoadOwnedMessageAsync(context, messageId, listenerUserId, cancellationToken);
        if (message is null)
        {
            return false;
        }

        message.IsHiddenByListener = true;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ReportAsync(
        int messageId,
        int listenerUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || !ReportReasonTypes.All.Contains(reason))
        {
            return false;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var message = await LoadOwnedMessageAsync(context, messageId, listenerUserId, cancellationToken);
        if (message is null)
        {
            return false;
        }

        if (message.IsReported)
        {
            // Reporting twice is not an error, it just does not reopen a resolved report.
            return true;
        }

        message.IsReported = true;
        message.ReportReason = reason;
        message.ReportedAtUtc = DateTime.UtcNow;
        message.ModerationResolvedAtUtc = null;
        message.ModerationAccepted = null;

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Artist message {MessageId} reported for {Reason}.",
            messageId,
            reason);

        return true;
    }

    /// <summary>
    /// Loads a message only if it was sent to this listener. Every listener-side mutation goes
    /// through here so ownership is checked once rather than four times.
    /// </summary>
    private static Task<ArtistFollowerMessage?> LoadOwnedMessageAsync(
        AppDbContext context,
        int messageId,
        int listenerUserId,
        CancellationToken cancellationToken)
    {
        return context.ArtistFollowerMessages
            .FirstOrDefaultAsync(
                message => message.Id == messageId
                           && message.ArtistFollower.ListenerUserId == listenerUserId,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> SendPendingEmailsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var pending = await context.ArtistFollowerMessages
            .Where(message => message.EmailSentDateUtc == null && !message.IsHiddenByListener)
            .Select(message => new PendingMessageEmail
            {
                MessageId = message.Id,
                ArtistName = message.ArtistFollower.CreatorPersona.Name,
                MessageText = message.MessageText,
                RecipientEmail = message.ArtistFollower.ListenerUser.Email,
                WantsEmail = message.ArtistFollower.ListenerUser.ReceiveArtistMessageEmails,
                EmailConfirmed = message.ArtistFollower.ListenerUser.EmailConfirmed,
                IsSuspended = message.ArtistFollower.ListenerUser.IsSuspended,
                MessagesMuted = !message.ArtistFollower.ArtistMessagesEnabled,
                IsBlocked = message.ArtistFollower.IsBlockedByListener,
            })
            .ToListAsync();

        if (pending.Count == 0)
        {
            return 0;
        }

        // Rows that must never be emailed are stamped anyway, so the job stops reconsidering them
        // every 15 minutes forever. The in-app message is unaffected - it is the row itself.
        var skipped = pending
            .Where(item => !item.ShouldSend)
            .Select(item => item.MessageId)
            .ToList();

        if (skipped.Count > 0)
        {
            await context.ArtistFollowerMessages
                .Where(message => skipped.Contains(message.Id))
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(message => message.EmailSentDateUtc, DateTime.UtcNow));

            _logger.LogInformation(
                "Skipped {Count} artist message emails (opted out, unconfirmed, muted or blocked).",
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
                    $"{item.ArtistName} sent you a message",
                    BuildMessageEmailBody(item, baseUrl));

                if (delivered)
                {
                    sent++;
                }
                else
                {
                    _logger.LogWarning("Failed to email artist message {MessageId}.", item.MessageId);
                }

                // Stamped whether or not the send succeeded. A permanent failure - a dead mailbox -
                // would otherwise be retried every 15 minutes for the life of the row, and the
                // listener has the message in-app regardless.
                await context.ArtistFollowerMessages
                    .Where(message => message.Id == item.MessageId)
                    .ExecuteUpdateAsync(setters =>
                        setters.SetProperty(message => message.EmailSentDateUtc, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error emailing artist message {MessageId}.", item.MessageId);
            }

            if (index == sendable.Count - 1)
            {
                break;
            }

            await Task.Delay((index + 1) % EmailBatchSize == 0 ? DelayBetweenBatchesMs : DelayBetweenEmailsMs);
        }

        _logger.LogInformation("Sent {Sent} of {Total} artist message emails.", sent, sendable.Count);
        return sent;
    }

    private string BuildMessageEmailBody(PendingMessageEmail item, string baseUrl)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var artist = HttpUtility.HtmlEncode(item.ArtistName);
        var text = HttpUtility.HtmlEncode(item.MessageText);
        var messagesUrl = $"{trimmedBase}{AppPageRoutes.ManageAccountArtistMessages}";
        var preferencesUrl = $"{trimmedBase}{AppPageRoutes.ManageAccount}";

        return $"""
                <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
                    <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                        {_emailService.GetEmailLogoHtml()}
                        <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>A message from {artist}</h1>
                    </div>
                    <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                        <p style='font-size: 16px; color: #333;'>{artist} sent you a message on StreamTunes:</p>
                        <blockquote style='margin: 20px 0; padding: 15px 20px; border-left: 4px solid #1a1a2e; background-color: #f7f7f9; color: #333; font-size: 16px;'>{text}</blockquote>
                        <div style='text-align: center; margin: 30px 0;'>
                            <a href='{messagesUrl}' style='display: inline-block; padding: 15px 30px; background-color: #1a1a2e; color: white; text-decoration: none; border-radius: 5px; font-size: 16px;'>View your messages</a>
                        </div>
                        <p style='color: #666; font-size: 14px;'>You are getting this because you follow {artist} on StreamTunes. They cannot see your email address.</p>
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
    private sealed class PendingMessageEmail
    {
        public int MessageId { get; init; }
        public string? ArtistName { get; init; }
        public string MessageText { get; init; } = string.Empty;
        public string? RecipientEmail { get; init; }
        public bool WantsEmail { get; init; }
        public bool EmailConfirmed { get; init; }
        public bool IsSuspended { get; init; }
        public bool MessagesMuted { get; init; }
        public bool IsBlocked { get; init; }

        public bool ShouldSend =>
            WantsEmail
            && EmailConfirmed
            && !IsSuspended
            && !MessagesMuted
            && !IsBlocked
            && !string.IsNullOrWhiteSpace(RecipientEmail);
    }
}
