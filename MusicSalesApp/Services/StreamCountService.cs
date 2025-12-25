using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for tracking and managing song stream counts.
/// </summary>
public class StreamCountService : IStreamCountService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<StreamCountService> _logger;

    public StreamCountService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<StreamCountService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public event Action<int, int> OnStreamCountUpdated;

    /// <inheritdoc />
    public async Task<int> IncrementStreamCountAsync(int songMetadataId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        int newCount;
        
        // Check if we're using a relational database (supports raw SQL)
        if (context.Database.IsRelational())
        {
            // Use ExecuteSqlRawAsync for atomic update in production.
            // Note: {0} is a parameterized placeholder, not string interpolation.
            // EF Core properly parameterizes the query to prevent SQL injection.
            var rowsAffected = await context.Database.ExecuteSqlRawAsync(
                "UPDATE SongMetadata SET NumberOfStreams = NumberOfStreams + 1 WHERE Id = {0}",
                songMetadataId);

            if (rowsAffected == 0)
            {
                _logger.LogWarning("Attempted to increment stream count for non-existent song metadata ID {SongMetadataId}", songMetadataId);
                return 0;
            }

            // Get the new count
            newCount = await context.SongMetadata
                .Where(s => s.Id == songMetadataId)
                .Select(s => s.NumberOfStreams)
                .FirstOrDefaultAsync();
        }
        else
        {
            // Fallback for in-memory database (testing)
            var song = await context.SongMetadata.FindAsync(songMetadataId);
            if (song == null)
            {
                _logger.LogWarning("Attempted to increment stream count for non-existent song metadata ID {SongMetadataId}", songMetadataId);
                return 0;
            }

            song.NumberOfStreams++;
            await context.SaveChangesAsync();
            newCount = song.NumberOfStreams;
        }

        _logger.LogDebug("Incremented stream count for song {SongMetadataId} to {NewCount}", songMetadataId, newCount);

        // Notify subscribers
        NotifyStreamCountUpdated(songMetadataId, newCount);

        return newCount;
    }

    /// <inheritdoc />
    public async Task<int> GetStreamCountAsync(int songMetadataId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        var count = await context.SongMetadata
            .Where(s => s.Id == songMetadataId)
            .Select(s => s.NumberOfStreams)
            .FirstOrDefaultAsync();

        return count;
    }

    /// <inheritdoc />
    public void NotifyStreamCountUpdated(int songMetadataId, int newCount)
    {
        OnStreamCountUpdated?.Invoke(songMetadataId, newCount);
    }
}
