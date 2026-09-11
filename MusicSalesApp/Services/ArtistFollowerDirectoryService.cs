#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class ArtistFollowerDirectoryService : IArtistFollowerDirectoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IArtistFollowerIdentityService _identityService;

    public ArtistFollowerDirectoryService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IArtistFollowerIdentityService identityService)
    {
        _dbContextFactory = dbContextFactory;
        _identityService = identityService;
    }

    /// <inheritdoc />
    public async Task<bool> OwnsPersonaAsync(
        int creatorPersonaId,
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await context.CreatorPersonas
            .AnyAsync(persona => persona.Id == creatorPersonaId && persona.CreatorId == creatorId, cancellationToken);
    }

    /// <summary>
    /// The name a follower chose to be seen under for this follow, or null - in which case the
    /// caller falls back to the pseudonym.
    /// </summary>
    /// <remarks>
    /// <b>There is exactly one link in this chain, deliberately.</b> It used to mirror the first
    /// two links of <c>SongMetadata.GetEffectiveArtistName()</c>, falling back to the creator's
    /// display name - but that chain answers "what shall we credit this song to", where any
    /// reasonable name beats none. This one answers "may we tell an artist who you are", where the
    /// absence of a chosen name IS the answer. Falling back was how a follower who chose Anonymous
    /// came to be named.
    ///
    /// <para>
    /// The same reasoning already ruled out that chain's third link, the creator's email with the
    /// domain stripped: it would put a fragment of a follower's email address in front of an
    /// artist, which is the one thing this whole feature promises never to do.
    /// </para>
    /// </remarks>
    private static string? ResolveFollowerArtistName(string? personaName) =>
        string.IsNullOrWhiteSpace(personaName) ? null : personaName;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtistFollowerSummaryDto>?> GetFollowersAsync(
        int creatorPersonaId,
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var owns = await context.CreatorPersonas
            .AnyAsync(persona => persona.Id == creatorPersonaId && persona.CreatorId == creatorId, cancellationToken);

        if (!owns)
        {
            return null;
        }

        // The projection is written out field by field rather than loading ArtistFollower rows and
        // mapping afterwards. That is the point: ListenerUserId is never selected, so it cannot
        // reach the caller even by accident.
        //
        // The two correlated sub-queries below DO read through the listener to their creator
        // record, but only to project a name that is already public. The id itself still never
        // leaves the query.
        var followers = await context.ArtistFollowers
            .AsNoTracking()
            .WhereActiveFollow()
            .Where(follow => follow.CreatorPersonaId == creatorPersonaId)
            .OrderByDescending(follow => follow.FollowedDateUtc)
            .Select(follow => new
            {
                follow.Id,
                follow.AnonymousListenerNumber,
                follow.FollowedDateUtc,
                follow.SourceSongMetadataId,
                SourceSongTitle = follow.SourceSongMetadata == null
                    ? null
                    : follow.SourceSongMetadata.SongTitle,
                SourceSongMp3BlobPath = follow.SourceSongMetadata == null
                    ? null
                    : follow.SourceSongMetadata.Mp3BlobPath,
                SourceSongBlobPath = follow.SourceSongMetadata == null
                    ? null
                    : follow.SourceSongMetadata.BlobPath,

                // A follower who is themselves an artist is shown under the name they already
                // publish as, rather than a pseudonym. A persona name appears on every song card,
                // so it discloses nothing new about the account - but only the person it belongs to
                // gets to decide that it appears HERE.
                //
                // This is the ONLY branch that can put a name on a follower. There used to be a
                // second one, falling back to the creator's display name for a consenting creator
                // with no enabled personas, and it was a leak: it never looked at FollowAsPersonaId,
                // so it named a follower who had deliberately chosen Anonymous the moment they
                // happened to disable their last persona - an identity change triggered by an
                // action that had nothing to do with consent. It also named a creator with no
                // personas at all, who is never shown the dialog and so never chose anything. A
                // follower with no chosen persona is a pseudonym, full stop.
                //
                // It is gated on the identity being publicly live RIGHT NOW: an inactive creator or
                // a suspended account is not publishing under that name any more, and falls back to
                // the pseudonym.
                // Every clause is required for a name to appear, and each one is a different way
                // for the answer to be "stay anonymous":
                //   - the follower picked this identity for THIS follow (FollowAsPersonaId)
                //   - their consent is still on RIGHT NOW - which is what makes withdrawal
                //     immediate and total, including for artists followed while it was on
                //   - the persona still exists and is still enabled
                //   - the creator is active and not suspended
                FollowerPersonaName = context.CreatorPersonas
                    .Where(persona => persona.Id == follow.FollowAsPersonaId
                                      && persona.IsEnabled
                                      && persona.Creator.UserId == follow.ListenerUserId
                                      && persona.Creator.IsActive
                                      && persona.Creator.RevealPersonaToFollowedArtists
                                      && (persona.Creator.User == null || !persona.Creator.User.IsSuspended))
                    .Select(persona => persona.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        if (followers.Count == 0)
        {
            return [];
        }

        var followerIds = followers.Select(follow => follow.Id).ToList();

        // A creator sees the text of a message they themselves sent, which discloses nothing new -
        // and lets the grid show what was said rather than only that something was.
        var messages = await context.ArtistFollowerMessages
            .AsNoTracking()
            .Where(message => followerIds.Contains(message.ArtistFollowerId))
            .Select(message => new { message.ArtistFollowerId, message.CreatedDateUtc, message.MessageText })
            .ToListAsync(cancellationToken);

        var lastMessageByFollower = messages
            .GroupBy(message => message.ArtistFollowerId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(message => message.CreatedDateUtc).First());

        return followers.Select(follow =>
        {
            var lastMessage = lastMessageByFollower.GetValueOrDefault(follow.Id);

            var artistName = ResolveFollowerArtistName(follow.FollowerPersonaName);

            return new ArtistFollowerSummaryDto(
                follow.Id,
                artistName ?? _identityService.FormatDisplayName(follow.AnonymousListenerNumber),
                artistName is not null,
                follow.FollowedDateUtc,
                follow.SourceSongMetadataId,
                follow.SourceSongMetadataId is null
                    ? null
                    : SongTitleHelper.GetEffectiveTitle(
                        follow.SourceSongTitle,
                        follow.SourceSongMp3BlobPath,
                        follow.SourceSongBlobPath),
                lastMessage is not null,
                lastMessage?.CreatedDateUtc,
                lastMessage?.MessageText);
        }).ToList();
    }
}
