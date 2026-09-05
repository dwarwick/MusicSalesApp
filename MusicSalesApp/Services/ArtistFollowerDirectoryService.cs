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
        // reach the caller even by accident, and the navigation to ApplicationUser is never
        // touched at all.
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

            return new ArtistFollowerSummaryDto(
                follow.Id,
                _identityService.FormatDisplayName(follow.AnonymousListenerNumber),
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
