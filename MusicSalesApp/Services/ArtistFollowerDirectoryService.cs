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
    /// The public artist name of a follower who is themselves a creator, or null for an ordinary
    /// listener - in which case the caller falls back to the pseudonym.
    /// </summary>
    /// <remarks>
    /// Persona first, then the creator display name, mirroring the first two links of
    /// <c>SongMetadata.GetEffectiveArtistName()</c>.
    ///
    /// <para>
    /// <b>It stops there deliberately.</b> That chain has a third link - the creator's email with
    /// the domain stripped - which is fine for a public song credit the account holder chose to
    /// publish under, and completely wrong here: it would put a fragment of a follower's email
    /// address in front of an artist, which is the one thing this whole feature promises never to
    /// do. A creator with no persona and no display name stays a pseudonym.
    /// </para>
    /// </remarks>
    private static string? ResolveFollowerArtistName(string? personaName, string? creatorDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(personaName))
        {
            return personaName;
        }

        return string.IsNullOrWhiteSpace(creatorDisplayName) ? null : creatorDisplayName;
    }

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
                // publish as, rather than a pseudonym. Both of these are public today - a persona
                // name appears on every song card, and a creator display name is what the artist
                // chain falls back to - so neither discloses anything new about the account.
                //
                // Both are gated on the identity being publicly live RIGHT NOW: an inactive
                // creator or a suspended account is not publishing under that name any more, and
                // falls back to the pseudonym.
                FollowerPersonaName = context.CreatorPersonas
                    .Where(persona => persona.Creator.UserId == follow.ListenerUserId
                                      && persona.IsEnabled
                                      && persona.Creator.IsActive
                                      && (persona.Creator.User == null || !persona.Creator.User.IsSuspended))
                    .OrderBy(persona => persona.Id)
                    .Select(persona => persona.Name)
                    .FirstOrDefault(),

                FollowerCreatorDisplayName = context.Creators
                    .Where(creator => creator.UserId == follow.ListenerUserId
                                      && creator.IsActive
                                      && (creator.User == null || !creator.User.IsSuspended))
                    .Select(creator => creator.DisplayName)
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

            var artistName = ResolveFollowerArtistName(
                follow.FollowerPersonaName, follow.FollowerCreatorDisplayName);

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
