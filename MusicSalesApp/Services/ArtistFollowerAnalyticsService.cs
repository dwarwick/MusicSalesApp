#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class ArtistFollowerAnalyticsService : IArtistFollowerAnalyticsService
{
    /// <summary>How many songs the "top songs generating follows" list shows.</summary>
    private const int TopSourceSongCount = 5;

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public ArtistFollowerAnalyticsService(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <inheritdoc />
    public async Task<ArtistFollowerAnalyticsDto?> GetAnalyticsAsync(
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

        var followers = context.ArtistFollowers
            .AsNoTracking()
            .WhereActiveFollow()
            .Where(follow => follow.CreatorPersonaId == creatorPersonaId);

        var total = await followers.CountAsync(cancellationToken);

        // Calendar month in UTC. The creator's own timezone would be a nicer read, but the figure
        // is a rough headline and a per-viewer month would make two creators looking at the same
        // persona see different numbers.
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var newThisMonth = await followers.CountAsync(
            follow => follow.FollowedDateUtc >= monthStart, cancellationToken);

        var topSongs = await followers
            .Where(follow => follow.SourceSongMetadataId != null)
            .GroupBy(follow => follow.SourceSongMetadataId!.Value)
            .Select(group => new { SongMetadataId = group.Key, FollowCount = group.Count() })
            .OrderByDescending(row => row.FollowCount)
            .Take(TopSourceSongCount)
            .ToListAsync(cancellationToken);

        var songIds = topSongs.Select(row => row.SongMetadataId).ToList();

        var titles = await context.SongMetadata
            .AsNoTracking()
            .Where(song => songIds.Contains(song.Id))
            .Select(song => new { song.Id, song.SongTitle, song.Mp3BlobPath, song.BlobPath })
            .ToListAsync(cancellationToken);

        var titleById = titles.ToDictionary(
            song => song.Id,
            song => SongTitleHelper.GetEffectiveTitle(song.SongTitle, song.Mp3BlobPath, song.BlobPath));

        var sources = topSongs
            .Select(row => new FollowSourceSongDto(
                row.SongMetadataId,
                titleById.GetValueOrDefault(row.SongMetadataId) ?? "Unknown song",
                row.FollowCount))
            .ToList();

        return new ArtistFollowerAnalyticsDto(total, newThisMonth, sources);
    }

    /// <inheritdoc />
    public async Task<int> GetTotalFollowersForCreatorAsync(
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await context.ArtistFollowers
            .AsNoTracking()
            .WhereActiveFollow()
            .CountAsync(follow => follow.CreatorPersona.CreatorId == creatorId, cancellationToken);
    }
}
