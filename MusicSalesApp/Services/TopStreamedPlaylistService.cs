using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Builds the five global "most streamed" playlists nightly and serves them back in rank order.
/// </summary>
public class TopStreamedPlaylistService : ITopStreamedPlaylistService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TopStreamedPlaylistService> _logger;

    public TopStreamedPlaylistService(
        IDbContextFactory<AppDbContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<TopStreamedPlaylistService> logger)
    {
        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task GenerateAllAsync()
    {
        foreach (var descriptor in TopStreamedPlaylists.All)
        {
            try
            {
                var songCount = await GenerateWindowAsync(descriptor);
                _logger.LogInformation(
                    "Generated top-streamed playlist {Window} with {SongCount} song(s)",
                    descriptor.Window,
                    songCount);
            }
            catch (Exception ex)
            {
                // One window failing must not cost the other four. Each is written by its own
                // SaveChanges, so a failure here leaves the previous playlist intact rather than
                // half-replaced.
                _logger.LogError(ex, "Failed to generate top-streamed playlist {Window}", descriptor.Window);
            }
        }
    }

    /// <summary>
    /// Replaces one playlist's rows. Returns how many songs the rebuilt playlist holds.
    /// </summary>
    private async Task<int> GenerateWindowAsync(TopStreamedWindowDescriptor descriptor)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var ranked = descriptor.Lookback.HasValue
            ? await RankByWindowedStreamsAsync(context, now - descriptor.Lookback.Value)
            : await RankByLifetimeStreamsAsync(context);

        var existing = await context.TopStreamedPlaylistEntries
            .Where(entry => entry.Window == descriptor.Window)
            .ToListAsync();
        context.TopStreamedPlaylistEntries.RemoveRange(existing);

        var generatedAt = now;
        var displayOrder = 1;
        foreach (var (songMetadataId, streamCount) in ranked)
        {
            context.TopStreamedPlaylistEntries.Add(new TopStreamedPlaylistEntry
            {
                Window = descriptor.Window,
                SongMetadataId = songMetadataId,
                DisplayOrder = displayOrder++,
                StreamCount = streamCount,
                GeneratedAt = generatedAt
            });
        }

        // Unconditional, so a window that has gone quiet is emptied rather than left showing a stale
        // playlist - the same reason RecommendationService always saves.
        await context.SaveChangesAsync();

        return ranked.Count;
    }

    /// <summary>
    /// The top songs by stream events at or after <paramref name="cutoff"/>.
    /// </summary>
    /// <remarks>
    /// <c>SongStreams</c> only began recording on 2026-02-21, so a window longer than the log is
    /// really "since logging started". That is most visible on the yearly playlist, which will keep
    /// returning the same set as the 30-day playlist's superset until a full year of events has
    /// accumulated - and can therefore rank a song below one the all-time playlist puts above it, since
    /// that playlist reads the older lifetime counter instead.
    /// </remarks>
    private static async Task<List<(int SongMetadataId, int StreamCount)>> RankByWindowedStreamsAsync(
        AppDbContext context,
        DateTime cutoff)
    {
        // Over-fetch before filtering: a song can rank highly and still be ineligible (disabled, or
        // from a deactivated creator), so ranking exactly MaxSongs rows and then filtering would
        // quietly return a short playlist.
        var candidates = await context.SongStreams
            .Where(stream => stream.CreatedDate >= cutoff)
            .GroupBy(stream => stream.SongMetadataId)
            .Select(group => new
            {
                SongMetadataId = group.Key,
                StreamCount = group.Count()
            })
            .OrderByDescending(candidate => candidate.StreamCount)
            // Ties are broken by id so a rebuild with unchanged data produces an unchanged playlist,
            // rather than shuffling songs that happen to be level.
            .ThenBy(candidate => candidate.SongMetadataId)
            .Take(TopStreamedPlaylists.MaxSongs * TopStreamedPlaylists.CandidateMultiplier)
            .ToListAsync();

        if (candidates.Count == 0)
        {
            return [];
        }

        var candidateIds = candidates.Select(candidate => candidate.SongMetadataId).ToList();
        var eligibleIds = await context.SongMetadata
            .Where(song => candidateIds.Contains(song.Id))
            .WhereVisibleLibrarySongs()
            .Select(song => song.Id)
            .ToListAsync();

        var eligible = eligibleIds.ToHashSet();

        return candidates
            .Where(candidate => eligible.Contains(candidate.SongMetadataId))
            .Take(TopStreamedPlaylists.MaxSongs)
            .Select(candidate => (candidate.SongMetadataId, candidate.StreamCount))
            .ToList();
    }

    /// <summary>
    /// The top songs by lifetime streams.
    /// </summary>
    /// <remarks>
    /// Ranks on <c>SongMetadata.NumberOfStreams</c>, not on a count of <c>SongStreams</c> rows. The
    /// counter shipped about two months before the events table, so for older songs the row count is
    /// short of the true lifetime total and an all-time playlist built from it would under-rank exactly
    /// the songs most likely to belong on it.
    /// </remarks>
    private static async Task<List<(int SongMetadataId, int StreamCount)>> RankByLifetimeStreamsAsync(
        AppDbContext context)
    {
        var top = await context.SongMetadata
            .WhereVisibleLibrarySongs()
            // A song nobody has played does not belong on a "most streamed" playlist. Without this the
            // all-time list would pad itself to ten with zero-stream songs while the four rolling
            // playlists correctly showed fewer.
            .Where(song => song.NumberOfStreams > 0)
            .OrderByDescending(song => song.NumberOfStreams)
            .ThenBy(song => song.Id)
            .Take(TopStreamedPlaylists.MaxSongs)
            .Select(song => new { song.Id, song.NumberOfStreams })
            .ToListAsync();

        return top
            .Select(song => (song.Id, song.NumberOfStreams))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<List<TopStreamedPlaylistEntry>> GetAsync(string window)
    {
        var descriptor = TopStreamedPlaylists.Find(window);
        if (descriptor is null)
        {
            return [];
        }

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Composed from the shared filter rather than restating it, so this cannot drift from
            // what the generator considered eligible.
            var visibleSongIds = context.SongMetadata
                .WhereVisibleLibrarySongs()
                .Select(song => song.Id);

            return await context.TopStreamedPlaylistEntries
                .Include(entry => entry.SongMetadata)
                    .ThenInclude(song => song.Creator)
                        .ThenInclude(creator => creator.User)
                // Without this the mobile mapper sees a null Persona and silently omits the artist
                // image and its rendition from every song. Lazy-loading proxies are off.
                .Include(entry => entry.SongMetadata)
                    .ThenInclude(song => song.Persona)
                .Where(entry => entry.Window == descriptor.Window)
                .Where(entry => visibleSongIds.Contains(entry.SongMetadataId))
                .OrderBy(entry => entry.DisplayOrder)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading top-streamed playlist {Window}", descriptor.Window);
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, int>> GetCountsAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var visibleSongIds = context.SongMetadata
                .WhereVisibleLibrarySongs()
                .Select(song => song.Id);

            var counts = await context.TopStreamedPlaylistEntries
                .Where(entry => visibleSongIds.Contains(entry.SongMetadataId))
                .GroupBy(entry => entry.Window)
                .Select(group => new { Window = group.Key, Count = group.Count() })
                .ToListAsync();

            return counts
                .Where(entry => entry.Count > 0)
                .ToDictionary(entry => entry.Window, entry => entry.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading top-streamed playlist counts");
            return [];
        }
    }
}
