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
