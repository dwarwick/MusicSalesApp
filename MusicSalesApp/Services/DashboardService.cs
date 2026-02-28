using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for providing dashboard analytics data for creators.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public DashboardService(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<List<StreamDataPoint>> GetStreamDataAsync(int creatorId, DateTime startUtc, DateTime endUtc, StreamInterval interval,
        HashSet<string> genres = null, HashSet<string> artists = null, HashSet<string> songTitles = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var hasFilters = (genres != null && genres.Count > 0) ||
                         (artists != null && artists.Count > 0) ||
                         (songTitles != null && songTitles.Count > 0);

        List<DateTime> streams;

        if (hasFilters)
        {
            // Get the matching SongMetadataIds first, then filter streams
            var metadataIds = await GetFilteredSongMetadataIdsAsync(context, creatorId, genres, artists, songTitles);

            streams = await context.SongStreams
                .Where(s => s.CreatorId == creatorId && s.CreatedDate >= startUtc && s.CreatedDate <= endUtc)
                .Where(s => metadataIds.Contains(s.SongMetadataId))
                .Select(s => s.CreatedDate)
                .ToListAsync();
        }
        else
        {
            streams = await context.SongStreams
                .Where(s => s.CreatorId == creatorId && s.CreatedDate >= startUtc && s.CreatedDate <= endUtc)
                .Select(s => s.CreatedDate)
                .ToListAsync();
        }

        var grouped = streams
            .GroupBy(d => TruncateToInterval(d, interval))
            .Select(g => new StreamDataPoint
            {
                PeriodStart = g.Key,
                StreamCount = g.Count()
            })
            .OrderBy(dp => dp.PeriodStart)
            .ToList();

        // Fill in missing periods with zero counts
        var allPeriods = GeneratePeriods(startUtc, endUtc, interval);
        var dataMap = grouped.ToDictionary(dp => dp.PeriodStart, dp => dp.StreamCount);

        var result = allPeriods.Select(p => new StreamDataPoint
        {
            PeriodStart = p,
            StreamCount = dataMap.GetValueOrDefault(p, 0)
        }).ToList();

        return result;
    }

    private static async Task<HashSet<int>> GetFilteredSongMetadataIdsAsync(AppDbContext context, int creatorId,
        HashSet<string> genres, HashSet<string> artists, HashSet<string> songTitles)
    {
        var query = context.SongMetadata
            .Where(sm => sm.CreatorId == creatorId && !string.IsNullOrEmpty(sm.Mp3BlobPath));

        // Apply genre filter at the database level
        if (genres != null && genres.Count > 0)
        {
            query = query.Where(sm => sm.Genre != null && genres.Contains(sm.Genre));
        }

        // Need Creator and User navigation properties for artist name derivation
        var songs = await query
            .Include(sm => sm.Creator)
                .ThenInclude(c => c.User)
            .ToListAsync();

        // Apply artist and song title filters in memory to use derivation logic
        var filtered = songs.AsEnumerable();

        if (artists != null && artists.Count > 0)
        {
            filtered = filtered.Where(sm => artists.Contains(sm.GetEffectiveArtistName()));
        }

        if (songTitles != null && songTitles.Count > 0)
        {
            filtered = filtered.Where(sm =>
            {
                var title = GetEffectiveSongTitle(sm);
                return songTitles.Contains(title);
            });
        }

        return filtered.Select(sm => sm.Id).ToHashSet();
    }

    /// <summary>
    /// Gets the effective display title for a song.
    /// Priority: SongTitle > filename derived from Mp3BlobPath
    /// </summary>
    internal static string GetEffectiveSongTitle(SongMetadata sm)
    {
        if (!string.IsNullOrEmpty(sm.SongTitle))
            return sm.SongTitle;

        return Path.GetFileNameWithoutExtension(sm.Mp3BlobPath ?? sm.ImageBlobPath ?? sm.BlobPath ?? "Unknown");
    }

    private static DateTime TruncateToInterval(DateTime date, StreamInterval interval)
    {
        return interval switch
        {
            StreamInterval.Hour => new DateTime(date.Year, date.Month, date.Day, date.Hour, 0, 0, DateTimeKind.Utc),
            StreamInterval.Day => new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc),
            StreamInterval.Week => GetStartOfWeek(date),
            StreamInterval.Month => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            StreamInterval.Year => new DateTime(date.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => date
        };
    }

    private static DateTime GetStartOfWeek(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        var monday = date.AddDays(-diff);
        return new DateTime(monday.Year, monday.Month, monday.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    private static List<DateTime> GeneratePeriods(DateTime startUtc, DateTime endUtc, StreamInterval interval)
    {
        var periods = new List<DateTime>();
        var current = TruncateToInterval(startUtc, interval);

        while (current <= endUtc)
        {
            periods.Add(current);
            current = interval switch
            {
                StreamInterval.Hour => current.AddHours(1),
                StreamInterval.Day => current.AddDays(1),
                StreamInterval.Week => current.AddDays(7),
                StreamInterval.Month => current.AddMonths(1),
                StreamInterval.Year => current.AddYears(1),
                _ => current.AddDays(1)
            };
        }

        return periods;
    }
}
