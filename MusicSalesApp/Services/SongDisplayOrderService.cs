using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

public interface ISongDisplayOrderService
{
    Task RandomizeDisplayOrderAsync();
}

public class SongDisplayOrderService : ISongDisplayOrderService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<SongDisplayOrderService> _logger;

    public SongDisplayOrderService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<SongDisplayOrderService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task RandomizeDisplayOrderAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var songs = await context.SongMetadata
            .Include(song => song.Creator)
            .WhereVisibleLibrarySongs()
            .ToListAsync();

        if (songs.Count == 0)
        {
            _logger.LogInformation("No eligible songs found for display-order randomization");
            return;
        }

        var randomizedSongs = songs
            .OrderBy(_ => Guid.NewGuid())
            .ToList();

        var updatedAt = DateTime.UtcNow;
        for (var index = 0; index < randomizedSongs.Count; index++)
        {
            randomizedSongs[index].DisplayOrder = index + 1;
            randomizedSongs[index].UpdatedAt = updatedAt;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Randomized display order for {SongCount} songs at {UpdatedAt}",
            randomizedSongs.Count,
            updatedAt);
    }
}