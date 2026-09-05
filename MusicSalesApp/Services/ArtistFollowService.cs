#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class ArtistFollowService : IArtistFollowService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IArtistFollowerIdentityService _identityService;
    private readonly ILogger<ArtistFollowService> _logger;

    public ArtistFollowService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IArtistFollowerIdentityService identityService,
        ILogger<ArtistFollowService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _identityService = identityService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ArtistFollowOutcome> SetFollowStateAsync(
        int creatorPersonaId,
        int listenerUserId,
        bool following,
        int? sourceSongMetadataId = null,
        int? followAsPersonaId = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.ArtistFollowers
            .FirstOrDefaultAsync(
                follow => follow.CreatorPersonaId == creatorPersonaId
                          && follow.ListenerUserId == listenerUserId,
                cancellationToken);

        if (!following)
        {
            // Unfollowing needs no availability check. A listener must be able to walk away from a
            // suspended or deleted artist, and refusing here would strand them.
            if (existing is null || !existing.IsActive)
            {
                return ArtistFollowOutcome.NotFollowing;
            }

            existing.IsActive = false;
            existing.UnfollowedDateUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return ArtistFollowOutcome.Unfollowed;
        }

        if (existing is { IsBlockedByListener: true })
        {
            // Following again would otherwise be a way to undo a block by accident.
            return ArtistFollowOutcome.Blocked;
        }

        var personaIsAvailable = await context.CreatorPersonas
            .WherePubliclyActive()
            .AnyAsync(persona => persona.Id == creatorPersonaId, cancellationToken);

        if (!personaIsAvailable)
        {
            return ArtistFollowOutcome.ArtistUnavailable;
        }

        // Following your own persona is meaningless, and it would show up in the creator's own
        // follower list, follower count and "new this month" figure. The UI hides the control on
        // your own songs, but this is the guard that matters: the mobile API takes a persona id
        // from the client, so the check has to live where the decision is made.
        var isOwnPersona = await context.CreatorPersonas
            .AnyAsync(
                persona => persona.Id == creatorPersonaId && persona.Creator.UserId == listenerUserId,
                cancellationToken);

        if (isOwnPersona)
        {
            return ArtistFollowOutcome.CannotFollowSelf;
        }

        var sourceSongId = await ResolveSourceSongAsync(context, creatorPersonaId, sourceSongMetadataId, cancellationToken);
        var followAsId = await ResolveFollowAsPersonaAsync(context, listenerUserId, followAsPersonaId, cancellationToken);

        if (existing is not null)
        {
            if (existing.IsActive)
            {
                return ArtistFollowOutcome.AlreadyFollowing;
            }

            // Re-following reactivates the original row rather than inserting a new one. That is
            // what keeps the pseudonym stable: to the creator this is the same Listener #4817
            // coming back, which is the point of a stable identifier.
            existing.IsActive = true;
            existing.UnfollowedDateUtc = null;
            existing.SourceSongMetadataId ??= sourceSongId;

            // Overwritten rather than kept: re-following is a fresh decision, and the listener may
            // have chosen a different identity - or none - this time.
            existing.FollowAsPersonaId = followAsId;

            await context.SaveChangesAsync(cancellationToken);
            return ArtistFollowOutcome.Followed;
        }

        var usedNumbers = await context.ArtistFollowers
            .Where(follow => follow.CreatorPersonaId == creatorPersonaId)
            .Select(follow => follow.AnonymousListenerNumber)
            .ToListAsync(cancellationToken);

        var follower = new ArtistFollower
        {
            CreatorPersonaId = creatorPersonaId,
            ListenerUserId = listenerUserId,
            FollowedDateUtc = DateTime.UtcNow,
            SourceSongMetadataId = sourceSongId,
            FollowAsPersonaId = followAsId,
            IsActive = true,
            AnonymousListenerNumber = _identityService.AllocateNumber(usedNumbers.ToHashSet()),
        };

        context.ArtistFollowers.Add(follower);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return ArtistFollowOutcome.Followed;
        }
        catch (DbUpdateException ex)
        {
            // Two clicks, two tabs, or a replayed offline intent racing each other. The unique
            // index on (CreatorPersonaId, ListenerUserId) is what turns that into a losable race
            // instead of a duplicate follow, and the loser's answer is simply "already following".
            // The pseudonym index can lose the same way; re-reading covers both.
            _logger.LogDebug(
                ex,
                "Concurrent follow insert for persona {PersonaId}; re-reading the winning row.",
                creatorPersonaId);

            context.Entry(follower).State = EntityState.Detached;

            var winner = await context.ArtistFollowers
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    row => row.CreatorPersonaId == creatorPersonaId && row.ListenerUserId == listenerUserId,
                    cancellationToken);

            if (winner is null)
            {
                throw;
            }

            return winner.IsActive ? ArtistFollowOutcome.AlreadyFollowing : ArtistFollowOutcome.NotFollowing;
        }
    }

    /// <summary>
    /// Keeps the recorded source song honest: a song id is only stored when that song really
    /// belongs to the persona being followed. Otherwise a caller could attribute a follow to any
    /// song at all, and the creator's "Followed After Listening To" column would be fiction.
    /// </summary>
    private static async Task<int?> ResolveSourceSongAsync(
        AppDbContext context,
        int creatorPersonaId,
        int? sourceSongMetadataId,
        CancellationToken cancellationToken)
    {
        if (sourceSongMetadataId is not > 0)
        {
            return null;
        }

        var belongsToPersona = await context.SongMetadata
            .AnyAsync(
                song => song.Id == sourceSongMetadataId && song.PersonaId == creatorPersonaId,
                cancellationToken);

        return belongsToPersona ? sourceSongMetadataId : null;
    }

    /// <summary>
    /// Validates the identity a listener asked to follow as, returning null for anonymous.
    /// </summary>
    /// <remarks>
    /// Refuses unless the listener has consented AND the persona is genuinely theirs and enabled.
    /// A client could otherwise pass any persona id and attribute its follow to a stranger, which
    /// would be worse than a privacy leak - it would be impersonation.
    /// </remarks>
    private static async Task<int?> ResolveFollowAsPersonaAsync(
        AppDbContext context,
        int listenerUserId,
        int? followAsPersonaId,
        CancellationToken cancellationToken)
    {
        if (followAsPersonaId is not > 0)
        {
            return null;
        }

        var isOwnedAndConsented = await context.CreatorPersonas
            .AnyAsync(
                persona => persona.Id == followAsPersonaId
                           && persona.IsEnabled
                           && persona.Creator.UserId == listenerUserId
                           && persona.Creator.IsActive
                           && persona.Creator.RevealPersonaToFollowedArtists,
                cancellationToken);

        return isOwnedAndConsented ? followAsPersonaId : null;
    }

    /// <inheritdoc />
    public async Task<FollowAsOptionsDto> GetFollowAsOptionsAsync(
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var consents = await context.Creators
            .AnyAsync(
                creator => creator.UserId == listenerUserId
                           && creator.IsActive
                           && creator.RevealPersonaToFollowedArtists,
                cancellationToken);

        if (!consents)
        {
            // No consent, no choice, no dialog - the follow is anonymous and the caller does not
            // need to know why.
            return new FollowAsOptionsDto(false, []);
        }

        var personas = await context.CreatorPersonas
            .AsNoTracking()
            .Where(persona => persona.Creator.UserId == listenerUserId && persona.IsEnabled)
            .OrderBy(persona => persona.Name)
            .Select(persona => new FollowAsPersonaDto(persona.Id, persona.Name))
            .ToListAsync(cancellationToken);

        return new FollowAsOptionsDto(true, personas);
    }

    /// <inheritdoc />
    public async Task<bool> IsFollowingAsync(
        int creatorPersonaId,
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await context.ArtistFollowers
            .WhereActiveFollow()
            .AnyAsync(
                follow => follow.CreatorPersonaId == creatorPersonaId && follow.ListenerUserId == listenerUserId,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<int>> GetFollowedPersonaIdsAsync(
        IEnumerable<int> creatorPersonaIds,
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        var ids = creatorPersonaIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0)
        {
            return new HashSet<int>();
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var followed = await context.ArtistFollowers
            .WhereActiveFollow()
            .Where(follow => follow.ListenerUserId == listenerUserId && ids.Contains(follow.CreatorPersonaId))
            .Select(follow => follow.CreatorPersonaId)
            .ToListAsync(cancellationToken);

        return followed.ToHashSet();
    }

    /// <inheritdoc />
    public async Task<int> GetFollowerCountAsync(int creatorPersonaId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await context.ArtistFollowers
            .WhereActiveFollow()
            .CountAsync(follow => follow.CreatorPersonaId == creatorPersonaId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> GetFollowerCountsAsync(
        IEnumerable<int> creatorPersonaIds,
        CancellationToken cancellationToken = default)
    {
        var ids = creatorPersonaIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var counts = await context.ArtistFollowers
            .WhereActiveFollow()
            .Where(follow => ids.Contains(follow.CreatorPersonaId))
            .GroupBy(follow => follow.CreatorPersonaId)
            .Select(group => new { PersonaId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var result = ids.ToDictionary(id => id, _ => 0);
        foreach (var row in counts)
        {
            result[row.PersonaId] = row.Count;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FollowedArtistDto>> GetFollowedArtistsAsync(
        int listenerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Blocked artists are included here on purpose - this list is also where a listener goes
        // to undo a block, so hiding them would make the action unreachable.
        //
        // That needs saying twice, because blocking sets IsActive to false: an IsActive-only filter
        // reads as correct, passes every service test, and silently strands the block. The Unblock
        // button existed for a release without a row it could ever render on.
        var follows = await context.ArtistFollowers
            .AsNoTracking()
            .Include(follow => follow.CreatorPersona)
            .Where(follow => follow.ListenerUserId == listenerUserId
                             && (follow.IsActive || follow.IsBlockedByListener))
            .OrderByDescending(follow => follow.FollowedDateUtc)
            .ToListAsync(cancellationToken);

        if (follows.Count == 0)
        {
            return [];
        }

        var personaIds = follows.Select(follow => follow.CreatorPersonaId).Distinct().ToList();
        var followerIds = follows.Select(follow => follow.Id).ToList();

        var latestReleases = await context.SongMetadata
            .AsNoTracking()
            .WherePubliclyReleased()
            .Where(song => personaIds.Contains(song.PersonaId!.Value))
            .Select(song => new
            {
                PersonaId = song.PersonaId!.Value,
                song.Id,
                song.SongTitle,
                song.Mp3BlobPath,
                song.BlobPath,
                Released = song.FirstPublishedAtUtc ?? song.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var latestByPersona = latestReleases
            .GroupBy(song => song.PersonaId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(song => song.Released).First());

        var unreadCounts = await context.ArtistFollowerMessages
            .AsNoTracking()
            .Where(message => followerIds.Contains(message.ArtistFollowerId)
                              && message.ReadDateUtc == null
                              && !message.IsHiddenByListener)
            .GroupBy(message => message.ArtistFollowerId)
            .Select(group => new { FollowerId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.FollowerId, row => row.Count, cancellationToken);

        return follows.Select(follow =>
        {
            latestByPersona.TryGetValue(follow.CreatorPersonaId, out var latest);

            return new FollowedArtistDto(
                follow.Id,
                follow.CreatorPersonaId,
                follow.CreatorPersona?.Name ?? ArtistDisplayNames.UnknownArtist,
                follow.CreatorPersona?.ImageBlobPath,
                follow.FollowedDateUtc,
                latest?.Id,
                latest is null
                    ? null
                    : SongTitleHelper.GetEffectiveTitle(latest.SongTitle, latest.Mp3BlobPath, latest.BlobPath),
                latest?.Released,
                follow.ReleaseNotificationsEnabled,
                follow.ArtistMessagesEnabled,
                follow.IsBlockedByListener,
                unreadCounts.GetValueOrDefault(follow.Id));
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> SetArtistNotificationPreferencesAsync(
        int creatorPersonaId,
        int listenerUserId,
        bool? releaseNotificationsEnabled,
        bool? artistMessagesEnabled,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var follow = await context.ArtistFollowers
            .FirstOrDefaultAsync(
                row => row.CreatorPersonaId == creatorPersonaId && row.ListenerUserId == listenerUserId,
                cancellationToken);

        if (follow is null)
        {
            return false;
        }

        if (releaseNotificationsEnabled.HasValue)
        {
            follow.ReleaseNotificationsEnabled = releaseNotificationsEnabled.Value;
        }

        if (artistMessagesEnabled.HasValue)
        {
            follow.ArtistMessagesEnabled = artistMessagesEnabled.Value;
        }

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetBlockedAsync(
        int creatorPersonaId,
        int listenerUserId,
        bool blocked,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var follow = await context.ArtistFollowers
            .FirstOrDefaultAsync(
                row => row.CreatorPersonaId == creatorPersonaId && row.ListenerUserId == listenerUserId,
                cancellationToken);

        if (follow is null)
        {
            return false;
        }

        follow.IsBlockedByListener = blocked;

        if (blocked)
        {
            follow.BlockedDateUtc = DateTime.UtcNow;

            // Blocking implies unfollowing. Leaving the follow active would keep the listener in
            // the creator's follower count while silently discarding everything sent to them,
            // which misreports the audience to the creator as well as confusing the listener.
            if (follow.IsActive)
            {
                follow.IsActive = false;
                follow.UnfollowedDateUtc = DateTime.UtcNow;
            }
        }
        else
        {
            follow.BlockedDateUtc = null;
        }

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

}
