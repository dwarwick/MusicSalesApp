using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

public interface IFeaturedSongRotationService
{
    Task RotateFeaturedSongsAsync();
}

public class FeaturedSongRotationService : IFeaturedSongRotationService
{
    private const int FeaturedSongCount = 4;

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<FeaturedSongRotationService> _logger;

    public FeaturedSongRotationService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<FeaturedSongRotationService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task RotateFeaturedSongsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var songs = await context.SongMetadata
            .Include(song => song.Creator)
            .ToListAsync();

        var selectedSongIds = await context.SongMetadata
            .Include(song => song.Creator)
            .WhereVisibleLibrarySongs()
            .OrderBy(_ => Guid.NewGuid())
            .Take(FeaturedSongCount)
            .Select(song => song.Id)
            .ToListAsync();
        var selectedSongIdSet = selectedSongIds.ToHashSet();

        var updatedAt = DateTime.UtcNow;
        var changedCount = 0;
        foreach (var song in songs)
        {
            var shouldBeFeatured = selectedSongIdSet.Contains(song.Id);
            if (song.DisplayOnHomePage == shouldBeFeatured)
            {
                continue;
            }

            song.DisplayOnHomePage = shouldBeFeatured;
            song.UpdatedAt = updatedAt;
            changedCount++;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Rotated featured songs. FeaturedSongCount={FeaturedSongCount}; ChangedSongCount={ChangedSongCount}; FeaturedSongIds={FeaturedSongIds}",
            selectedSongIds.Count,
            changedCount,
            string.Join(",", selectedSongIds.OrderBy(id => id)));
    }
}
