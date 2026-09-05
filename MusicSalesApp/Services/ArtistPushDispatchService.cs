#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class ArtistPushDispatchService : IArtistPushDispatchService
{
    /// <summary>
    /// Rows considered per run. A cap rather than the whole backlog, so one enormous release
    /// cannot make a single run unbounded; what is left is picked up five minutes later.
    /// </summary>
    private const int BatchSize = 500;

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IPushDeviceTokenService _deviceTokenService;
    private readonly IPushNotificationSender _sender;
    private readonly IAppSettingsService _appSettingsService;
    private readonly ILogger<ArtistPushDispatchService> _logger;

    public ArtistPushDispatchService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IPushDeviceTokenService deviceTokenService,
        IPushNotificationSender sender,
        IAppSettingsService appSettingsService,
        ILogger<ArtistPushDispatchService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _deviceTokenService = deviceTokenService;
        _sender = sender;
        _appSettingsService = appSettingsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> DispatchPendingAsync()
    {
        // The admin kill switch, checked before anything else. Deliberately leaves every row
        // unstamped, exactly like an unconfigured transport: turning push on later delivers what
        // is still pending rather than having silently consumed it while the flag was off.
        if (!await _appSettingsService.IsPushNotificationsEnabledAsync())
        {
            _logger.LogDebug("Push notifications are switched off; skipping dispatch.");
            return 0;
        }

        if (!_sender.IsConfigured)
        {
            // Nothing configured. Leave every row unstamped so that configuring credentials later
            // delivers the backlog rather than silently skipping it, and say so once per run
            // rather than once per notification.
            _logger.LogDebug("No push transport is configured; skipping dispatch.");
            return 0;
        }

        var delivered = 0;
        delivered += await DispatchReleaseNotificationsAsync();
        delivered += await DispatchArtistMessagesAsync();

        return delivered;
    }

    private async Task<int> DispatchReleaseNotificationsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var pending = await context.ArtistReleaseNotifications
            .AsNoTracking()
            .Where(notification => notification.PushSentDateUtc == null)
            .OrderBy(notification => notification.CreatedDateUtc)
            .Take(BatchSize)
            .Select(notification => new PendingPush
            {
                EntityId = notification.Id,
                Kind = PushNotificationKinds.Release,
                ListenerUserId = notification.ListenerUserId,
                CreatorPersonaId = notification.CreatorPersonaId,
                ArtistName = notification.CreatorPersona.Name,
                SongMetadataId = notification.SongMetadataId,
                SongTitle = notification.SongMetadata.SongTitle,
                Mp3BlobPath = notification.SongMetadata.Mp3BlobPath,
                BlobPath = notification.SongMetadata.BlobPath,
                WantsPush = notification.ListenerUser.ReceiveArtistReleasePush,
                IsSuspended = notification.ListenerUser.IsSuspended,
                // A song pulled between the notification and this run must not be announced.
                SubjectIsAvailable = notification.SongMetadata.IsActive && notification.SongMetadata.IsEnabled,
                Muted = false,
                IsBlocked = false,
            })
            .ToListAsync();

        return await DeliverAsync(
            pending,
            ids => context.ArtistReleaseNotifications
                .Where(row => ids.Contains(row.Id))
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(row => row.PushSentDateUtc, DateTime.UtcNow)));
    }

    private async Task<int> DispatchArtistMessagesAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var pending = await context.ArtistFollowerMessages
            .AsNoTracking()
            .Where(message => message.PushSentDateUtc == null && !message.IsHiddenByListener)
            .OrderBy(message => message.CreatedDateUtc)
            .Take(BatchSize)
            .Select(message => new PendingPush
            {
                EntityId = message.Id,
                Kind = PushNotificationKinds.ArtistMessage,
                ListenerUserId = message.ArtistFollower.ListenerUserId,
                CreatorPersonaId = message.ArtistFollower.CreatorPersonaId,
                ArtistName = message.ArtistFollower.CreatorPersona.Name,
                MessageText = message.MessageText,
                WantsPush = message.ArtistFollower.ListenerUser.ReceiveArtistMessagePush,
                IsSuspended = message.ArtistFollower.ListenerUser.IsSuspended,
                SubjectIsAvailable = true,
                Muted = !message.ArtistFollower.ArtistMessagesEnabled,
                IsBlocked = message.ArtistFollower.IsBlockedByListener,
            })
            .ToListAsync();

        return await DeliverAsync(
            pending,
            ids => context.ArtistFollowerMessages
                .Where(row => ids.Contains(row.Id))
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(row => row.PushSentDateUtc, DateTime.UtcNow)));
    }

    /// <summary>
    /// Sends one batch and stamps what is settled.
    /// </summary>
    /// <remarks>
    /// The stamping rule is the whole point of this method, and it has three cases:
    /// a row nobody should be pushed is stamped immediately (or the job reconsiders it every five
    /// minutes forever); a row whose devices all answered is stamped; and a row where any device
    /// suffered a TRANSPORT failure is left alone so a later run tries again. A rejected token is
    /// settled, not deferred - retrying it can never succeed.
    /// </remarks>
    private async Task<int> DeliverAsync(
        List<PendingPush> pending,
        Func<List<int>, Task<int>> stamp)
    {
        if (pending.Count == 0)
        {
            return 0;
        }

        var skipped = pending.Where(item => !item.ShouldPush).Select(item => item.EntityId).ToList();

        if (skipped.Count > 0)
        {
            await stamp(skipped);
            _logger.LogInformation(
                "Skipped {Count} pushes (opted out, suspended, muted, blocked or subject withdrawn).",
                skipped.Count);
        }

        var sendable = pending.Where(item => item.ShouldPush).ToList();

        if (sendable.Count == 0)
        {
            return 0;
        }

        var tokensByUser = await _deviceTokenService.GetActiveTokensAsync(
            sendable.Select(item => item.ListenerUserId));

        var settled = new List<int>();
        var rejectedTokens = new List<string>();
        var delivered = 0;

        foreach (var item in sendable)
        {
            if (!tokensByUser.TryGetValue(item.ListenerUserId, out var devices) || devices.Count == 0)
            {
                // Wants push, has no device registered. Settled - there is nothing to wait for.
                settled.Add(item.EntityId);
                continue;
            }

            var message = BuildMessage(item);
            var anyDeferred = false;

            // Not grouped by platform: FCM delivers to Android and iOS tokens alike, so
            // PushDeviceToken.Platform is kept for diagnostics rather than for routing.
            var results = await _sender.SendAsync(
                message, devices.Select(device => device.Token).ToList());

            foreach (var result in results)
            {
                switch (result.Outcome)
                {
                    case PushDeliveryOutcome.Delivered:
                        delivered++;
                        break;

                    case PushDeliveryOutcome.TokenRejected:
                        rejectedTokens.Add(result.Token);
                        break;

                    case PushDeliveryOutcome.TransportFailure:
                        anyDeferred = true;
                        break;
                }
            }

            if (!anyDeferred)
            {
                settled.Add(item.EntityId);
            }
        }

        if (rejectedTokens.Count > 0)
        {
            await _deviceTokenService.DeactivateAsync(rejectedTokens, "Rejected by the push service");
        }

        if (settled.Count > 0)
        {
            await stamp(settled);
        }

        _logger.LogInformation(
            "Pushed to {Delivered} devices; settled {Settled} of {Total} notifications, retired {Rejected} tokens.",
            delivered,
            settled.Count,
            sendable.Count,
            rejectedTokens.Count);

        return delivered;
    }

    private static PushMessage BuildMessage(PendingPush item)
    {
        var artist = string.IsNullOrWhiteSpace(item.ArtistName)
            ? ArtistDisplayNames.UnknownArtist
            : item.ArtistName;

        var data = new Dictionary<string, string>
        {
            [PushDataKeys.Kind] = item.Kind,
            [PushDataKeys.PersonaId] = item.CreatorPersonaId.ToString(),
            [PushDataKeys.EntityId] = item.EntityId.ToString(),
        };

        if (item.Kind == PushNotificationKinds.Release)
        {
            var title = SongTitleHelper.GetEffectiveTitle(item.SongTitle, item.Mp3BlobPath, item.BlobPath);
            data[PushDataKeys.SongId] = item.SongMetadataId.ToString();

            return new PushMessage($"New music from {artist}", $"{artist} just released {title}.", data);
        }

        // The message text is shown as-is. It has already been through
        // ArtistMessageContentPolicy, so it cannot carry a link or an address into a
        // notification shade where nobody would think to check it.
        return new PushMessage($"{artist} sent you a message", item.MessageText ?? string.Empty, data);
    }

    /// <summary>
    /// One candidate push, flattened out of the query so no entity or open reader is held while
    /// the outbound HTTP calls run.
    /// </summary>
    private sealed class PendingPush
    {
        public int EntityId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public int ListenerUserId { get; init; }
        public int CreatorPersonaId { get; init; }
        public string? ArtistName { get; init; }
        public int SongMetadataId { get; init; }
        public string? SongTitle { get; init; }
        public string? Mp3BlobPath { get; init; }
        public string? BlobPath { get; init; }
        public string? MessageText { get; init; }
        public bool WantsPush { get; init; }
        public bool IsSuspended { get; init; }
        public bool SubjectIsAvailable { get; init; }
        public bool Muted { get; init; }
        public bool IsBlocked { get; init; }

        public bool ShouldPush =>
            WantsPush && !IsSuspended && SubjectIsAvailable && !Muted && !IsBlocked;
    }
}
