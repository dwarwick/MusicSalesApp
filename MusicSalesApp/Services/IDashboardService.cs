namespace MusicSalesApp.Services;

/// <summary>
/// Service for providing dashboard analytics data for creators.
/// </summary>
public interface IDashboardService
{
    /// <summary>
    /// Gets aggregated stream counts for a creator's songs within a time range, grouped by interval.
    /// </summary>
    /// <param name="creatorId">The creator's ID.</param>
    /// <param name="startUtc">Start of the time range (UTC).</param>
    /// <param name="endUtc">End of the time range (UTC).</param>
    /// <param name="interval">The grouping interval (Year, Month, Week, Day, Hour).</param>
    /// <param name="genres">Optional set of genres to filter by.</param>
    /// <param name="artists">Optional set of artist names to filter by.</param>
    /// <param name="songTitles">Optional set of song titles to filter by.</param>
    /// <returns>A list of data points with period start and stream count.</returns>
    Task<List<StreamDataPoint>> GetStreamDataAsync(int creatorId, DateTime startUtc, DateTime endUtc, StreamInterval interval,
        HashSet<string> genres = null, HashSet<string> artists = null, HashSet<string> songTitles = null);

    /// <summary>
    /// Gets the available filter options for a creator's streams within a time range.
    /// Only returns genres, artists, and song titles that have actual streams in the period.
    /// Uses cross-filtering: each filter dimension is filtered by the other dimensions' selections.
    /// </summary>
    /// <param name="creatorId">The creator's ID.</param>
    /// <param name="startUtc">Start of the time range (UTC).</param>
    /// <param name="endUtc">End of the time range (UTC).</param>
    /// <param name="selectedGenres">Currently selected genres (used to cross-filter artists and song titles).</param>
    /// <param name="selectedArtists">Currently selected artists (used to cross-filter genres and song titles).</param>
    /// <param name="selectedSongTitles">Currently selected song titles (used to cross-filter genres and artists).</param>
    /// <returns>Filter options with stream counts for each genre, artist, and song title.</returns>
    Task<StreamFilterOptions> GetStreamFilterOptionsAsync(int creatorId, DateTime startUtc, DateTime endUtc,
        HashSet<string> selectedGenres = null, HashSet<string> selectedArtists = null, HashSet<string> selectedSongTitles = null);
}

/// <summary>
/// Contains available filter options derived from actual stream data.
/// </summary>
public class StreamFilterOptions
{
    /// <summary>
    /// Genres with stream counts (e.g., "Rock" -> 5 means 5 streams for Rock songs).
    /// </summary>
    public Dictionary<string, int> Genres { get; set; } = new();

    /// <summary>
    /// Artist names with stream counts.
    /// </summary>
    public Dictionary<string, int> Artists { get; set; } = new();

    /// <summary>
    /// Song titles with stream counts.
    /// </summary>
    public Dictionary<string, int> SongTitles { get; set; } = new();
}

/// <summary>
/// Represents a single data point for stream count over time.
/// </summary>
public class StreamDataPoint
{
    /// <summary>
    /// The start of the time period (UTC).
    /// </summary>
    public DateTime PeriodStart { get; set; }

    /// <summary>
    /// The number of streams in this period.
    /// </summary>
    public int StreamCount { get; set; }
}

/// <summary>
/// The time interval for grouping stream data.
/// </summary>
public enum StreamInterval
{
    Hour,
    Day,
    Week,
    Month,
    Year
}
