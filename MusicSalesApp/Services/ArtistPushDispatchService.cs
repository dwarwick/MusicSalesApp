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

        // Loaded together rather than dispatched one kind after the other, because a listener on a
        // batched frequency gets ONE push covering whatever happened in the window - and what
        // happened may be a release and a message, not two of a kind.
        var pending = await LoadPendingReleasesAsync();
        pending.AddRange(await LoadPendingArtistMessagesAsync());

        return await DeliverAsync(pending);
    }

    private async Task<List<PendingPush>> LoadPendingReleasesAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ArtistReleaseNotifications
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
                CreatedDateUtc = notification.CreatedDateUtc,
                FrequencyValue = notification.ListenerUser.ArtistPushFrequency,
            })
            .ToListAsync();
    }

    private async Task<List<PendingPush>> LoadPendingArtistMessagesAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ArtistFollowerMessages
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
                CreatedDateUtc = message.CreatedDateUtc,
                FrequencyValue = message.ArtistFollower.ListenerUser.ArtistPushFrequency,
            })
            .ToListAsync();
    }

    /// <summary>
    /// Marks rows as pushed, sending each id back to the table it came from.
    /// </summary>
    private async Task StampAsync(IEnumerable<PendingPush> items)
    {
        var byKind = items.GroupBy(item => item.Kind).ToList();

        if (byKind.Count == 0)
        {
            return;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        foreach (var group in byKind)
        {
            var ids = group.Select(item => item.EntityId).ToList();

            if (group.Key == PushNotificationKinds.Release)
            {
                await context.ArtistReleaseNotifications
                    .Where(row => ids.Contains(row.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PushSentDateUtc, now));
            }
            else
            {
                await context.ArtistFollowerMessages
                    .Where(row => ids.Contains(row.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PushSentDateUtc, now));
            }
        }
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
    private async Task<int> DeliverAsync(List<PendingPush> pending)
    {
        if (pending.Count == 0)
        {
            return 0;
        }

        var skipped = pending.Where(item => !item.ShouldPush).ToList();

        if (skipped.Count > 0)
        {
            await StampAsync(skipped);
            _logger.LogInformation(
                "Skipped {Count} pushes (opted out, suspended, muted, blocked or subject withdrawn).",
                skipped.Count);
        }

        // One outgoing push per group. A listener on Instant gets a group per notification, which
        // is exactly the old behaviour; a listener on a window gets at most one group covering
        // everything that has waited long enough.
        var groups = BuildSendGroups(pending.Where(item => item.ShouldPush));

        if (groups.Count == 0)
        {
            return 0;
        }

        var sendable = groups.SelectMany(group => group).ToList();

        var tokensByUser = await _deviceTokenService.GetActiveTokensAsync(
            sendable.Select(item => item.ListenerUserId));

        var settled = new List<PendingPush>();
        var rejectedTokens = new List<string>();
        var delivered = 0;

        foreach (var group in groups)
        {
            var item = group[0];

            if (!tokensByUser.TryGetValue(item.ListenerUserId, out var devices) || devices.Count == 0)
            {
                // Wants push, has no device registered. Settled - there is nothing to wait for.
                settled.AddRange(group);
                continue;
            }

            var message = BuildMessage(group);
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
                // The whole group settles together. Half a digest is not a thing that can be
                // re-sent: the surviving rows would come back as a second, smaller summary.
                settled.AddRange(group);
            }
        }

        if (rejectedTokens.Count > 0)
        {
            await _deviceTokenService.DeactivateAsync(rejectedTokens, "Rejected by the push service");
        }

        if (settled.Count > 0)
        {
            await StampAsync(settled);
        }

        _logger.LogInformation(
            "Pushed to {Delivered} devices in {Groups} notifications; settled {Settled} of {Total} rows, retired {Rejected} tokens.",
            delivered,
            groups.Count,
            settled.Count,
            sendable.Count,
            rejectedTokens.Count);

        return delivered;
    }

    /// <summary>
    /// Decides what goes out as its own push and what is collapsed into a summary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instant is every row on its own, which is what everyone had before frequencies existed.
    /// On a window, a listener's rows are held until the OLDEST has waited the full window, then
    /// leave together as one push. So the setting is a cap on interruptions rather than a delay
    /// applied to each item: at most one push per window, however much happened inside it.
    /// </para>
    /// <para>
    /// Waiting for the oldest row means a listener on Daily waits up to a day for the first thing
    /// too. The alternative - send at once, then go quiet for the window - needs a
    /// last-sent-at column per user and makes the first push of a quiet period arrive alone; it is
    /// a reasonable change to make later, but it is not what "at most once a day" says.
    /// </para>
    /// </remarks>
    private static List<List<PendingPush>> BuildSendGroups(IEnumerable<PendingPush> sendable)
    {
        var now = DateTime.UtcNow;
        var groups = new List<List<PendingPush>>();

        foreach (var perListener in sendable.GroupBy(item => item.ListenerUserId))
        {
            // Read from the rows rather than passed in, because every row for one listener carries
            // that listener's own frequency.
            var frequency = ArtistPushFrequencies.FromValue(perListener.First().FrequencyValue);

            if (!ArtistPushFrequencies.IsBatched(frequency))
            {
                groups.AddRange(perListener.Select(item => new List<PendingPush> { item }));
                continue;
            }

            var batch = perListener.OrderBy(item => item.CreatedDateUtc).ToList();
            var waited = now - batch[0].CreatedDateUtc;

            if (waited < ArtistPushFrequencies.WindowFor(frequency))
            {
                // Left unstamped, so a later run reconsiders it. Nothing is consumed by waiting.
                continue;
            }

            groups.Add(batch);
        }

        return groups;
    }

    private static PushMessage BuildMessage(List<PendingPush> group) =>
        group.Count == 1 ? BuildSingleMessage(group[0]) : BuildDigestMessage(group);

    /// <summary>
    /// One push standing for several notifications.
    /// </summary>
    /// <remarks>
    /// The destination has to match what the text says, so the payload only names an artist when
    /// the summary does. A digest about one artist carries their name and opens that artist; a
    /// digest spanning several has no single destination and opens the app.
    /// </remarks>
    private static PushMessage BuildDigestMessage(List<PendingPush> group)
    {
        var artists = group
            .Select(item => string.IsNullOrWhiteSpace(item.ArtistName)
                ? ArtistDisplayNames.UnknownArtist
                : item.ArtistName!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var data = new Dictionary<string, string>
        {
            [PushDataKeys.Kind] = PushNotificationKinds.Digest,
            [PushDataKeys.Count] = group.Count.ToString(),
        };

        if (artists.Count > 1)
        {
            return new PushMessage(
                "Artists you follow",
                $"{group.Count} new updates from {artists.Count} artists you follow.",
                data);
        }

        var artist = artists[0];
        data[PushDataKeys.ArtistName] = artist;
        data[PushDataKeys.PersonaId] = group[0].CreatorPersonaId.ToString();

        var releases = group.Count(item => item.Kind == PushNotificationKinds.Release);

        if (releases == group.Count)
        {
            return new PushMessage(
                $"New music from {artist}",
                $"{artist} released {releases} new songs.",
                data);
        }

        return new PushMessage($"Updates from {artist}", $"{artist} has {group.Count} updates for you.", data);
    }

    private static PushMessage BuildSingleMessage(PendingPush item)
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

        /// <summary>When the notification happened - what the frequency window is measured from.</summary>
        public DateTime CreatedDateUtc { get; init; }

        /// <summary>The listener's ArtistPushFrequency, raw, because the column is an int.</summary>
        public int FrequencyValue { get; init; }

        public bool ShouldPush =>
            WantsPush && !IsSuspended && SubjectIsAvailable && !Muted && !IsBlocked;
    }
}
