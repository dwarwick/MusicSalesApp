using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for tracking and managing song stream counts.
/// </summary>
public class StreamCountService : IStreamCountService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<StreamCountService> _logger;
    private readonly IHubContext<StreamCountHub> _hubContext;

    public StreamCountService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<StreamCountService> logger,
        IHubContext<StreamCountHub> hubContext)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _hubContext = hubContext;
    }

    /// <inheritdoc />
    public event Action<int, int> OnStreamCountUpdated;

    /// <inheritdoc />
    public async Task<int> IncrementStreamCountAsync(int songMetadataId, int? streamerUserId = null, bool isAdmin = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        // Look up the song metadata to get CreatorId and check if streamer is the creator
        var song = await context.SongMetadata
            .Include(s => s.Creator)
            .FirstOrDefaultAsync(s => s.Id == songMetadataId);

        if (song == null)
        {
            _logger.LogWarning("Attempted to increment stream count for non-existent song metadata ID {SongMetadataId}", songMetadataId);
            return 0;
        }

        // Determine if the streamer is the creator of this song
        bool isCreatorOfSong = streamerUserId.HasValue 
            && song.Creator != null 
            && song.Creator.UserId == streamerUserId.Value;

        // Creators streaming their own songs and admins do not generate stream counts or records
        bool isCountableStream = !isAdmin && !isCreatorOfSong;

        int newCount;

        if (isCountableStream)
        {
            // Check if we're using a relational database (supports raw SQL)
            if (context.Database.IsRelational())
            {
                // Use ExecuteSqlRawAsync for atomic update in production.
                // Note: {0} is a parameterized placeholder, not string interpolation.
                // EF Core properly parameterizes the query to prevent SQL injection.
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE SongMetadata SET NumberOfStreams = NumberOfStreams + 1 WHERE Id = {0}",
                    songMetadataId);

                // Get the new count
                newCount = await context.SongMetadata
                    .Where(s => s.Id == songMetadataId)
                    .Select(s => s.NumberOfStreams)
                    .FirstOrDefaultAsync();
            }
            else
            {
                // Fallback for in-memory database (testing).
                // The tracked entity change is persisted by SaveChangesAsync below.
                song.NumberOfStreams++;
                newCount = song.NumberOfStreams;
            }

            // Create a SongStream record to correlate with the count increment
            var songStream = new Models.SongStream
            {
                SongMetadataId = songMetadataId,
                CreatorId = song.CreatorId,
                StreamerUserId = streamerUserId,
                CreatedDate = DateTime.UtcNow
            };
            context.SongStreams.Add(songStream);
            await context.SaveChangesAsync();
        }
        else
        {
            newCount = song.NumberOfStreams;
        }

        _logger.LogInformation("StreamCountService: Stream request for song {SongMetadataId} by user {StreamerUserId}, isAdmin={IsAdmin}, isCreatorOfSong={IsCreatorOfSong}, recorded={Recorded}. Count: {NewCount}", 
            songMetadataId, streamerUserId, isAdmin, isCreatorOfSong, isCountableStream, newCount);

        // Notify subscribers (local in-process event)
        NotifyStreamCountUpdated(songMetadataId, newCount);

        // Broadcast to all connected clients via SignalR for cross-tab updates
        await _hubContext.Clients.All.SendAsync("ReceiveStreamCountUpdate", songMetadataId, newCount);

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
