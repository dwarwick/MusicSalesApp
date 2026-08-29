namespace MusicSalesApp.Common.Helpers;

// This project does not enable nullable reference types project-wide; opted in here so Find() can
// say in the signature that it returns null for an unknown key.
#nullable enable

/// <summary>
/// Keys identifying the five global "most streamed" playlists.
///
/// <para>
/// These are written to <c>TopStreamedPlaylistEntries.Window</c> by the nightly job and read back by
/// every surface that renders a playlist - the web home page, My Playlists, the player route
/// <c>/top-streamed/{window}</c> and the mobile API. A writer/reader mismatch would silently produce
/// an empty playlist with no compiler error, which is exactly the class of bug AGENTS.md mandates
/// constants for.
/// </para>
///
/// <para>
/// They also appear in URLs, so treat them as a public contract: renaming one breaks any bookmark or
/// shared link pointing at that playlist.
/// </para>
/// </summary>
public static class TopStreamedWindows
{
    public const string Day = "Day";
    public const string Week = "Week";
    public const string Month = "Month";
    public const string Year = "Year";
    public const string AllTime = "AllTime";
}

/// <summary>
/// Everything that distinguishes one playlist from another, in one record.
/// </summary>
/// <param name="Window">The <see cref="TopStreamedWindows"/> key. Also the URL segment.</param>
/// <param name="Name">The playlist title shown to listeners.</param>
/// <param name="Description">The card subtitle.</param>
/// <param name="DisplayOrder">1-based position wherever the five are listed together.</param>
/// <param name="Lookback">
/// How far back the ranking counts streams, or <c>null</c> for the all-time playlist.
///
/// <para>
/// <b>Null is not "count everything from SongStreams".</b> The all-time playlist ranks on
/// <c>SongMetadata.NumberOfStreams</c> instead, because that counter predates the SongStreams table
/// by about two months - so for older songs the row count is short of the true lifetime total.
/// </para>
/// </param>
/// <param name="PeriodLabel">
/// Column heading for the period stream count in the player's track list, or <c>null</c> for the
/// all-time playlist.
///
/// <para>
/// A playlist's rows are ranked on streams inside its window, but the player's live counter shows
/// LIFETIME streams - so on "Top 10 Today" a correctly ordered list would look mis-sorted if only the
/// lifetime number were visible. The player therefore shows both, and this names the second column.
/// It is null for the all-time playlist because there the two numbers are the same, and a duplicate
/// column would be noise.
/// </para>
/// </param>
public sealed record TopStreamedWindowDescriptor(
    string Window,
    string Name,
    string Description,
    int DisplayOrder,
    TimeSpan? Lookback,
    string? PeriodLabel);

/// <summary>
/// The ordered definition of the five playlists. Every surface renders from this list rather than
/// hard-coding five tiles in source order, so the Day -> Week -> Month -> Year -> All Time sequence
/// is defined exactly once.
/// </summary>
public static class TopStreamedPlaylists
{
    /// <summary>How many songs a playlist holds at most. A playlist with fewer streamed songs is shorter.</summary>
    public const int MaxSongs = 10;

    /// <summary>
    /// How many candidates to pull before applying the visibility filter. A song can rank highly and
    /// still be ineligible (disabled, or from a deactivated creator), so ranking exactly
    /// <see cref="MaxSongs"/> rows and then filtering would quietly return a short playlist.
    /// </summary>
    public const int CandidateMultiplier = 3;

    public static IReadOnlyList<TopStreamedWindowDescriptor> All { get; } =
    [
        new(TopStreamedWindows.Day, "Top 10 Today", "The most streamed songs in the past 24 hours", 1, TimeSpan.FromDays(1), "Today"),
        new(TopStreamedWindows.Week, "Top 10 This Week", "The most streamed songs in the past week", 2, TimeSpan.FromDays(7), "This Week"),
        new(TopStreamedWindows.Month, "Top 10 This Month", "The most streamed songs in the past 30 days", 3, TimeSpan.FromDays(30), "This Month"),
        new(TopStreamedWindows.Year, "Top 10 This Year", "The most streamed songs in the past year", 4, TimeSpan.FromDays(365), "This Year"),
        new(TopStreamedWindows.AllTime, "Top 10 of All Time", "The most streamed songs ever on StreamTunes", 5, null, null)
    ];

    /// <summary>
    /// The descriptor for <paramref name="window"/>, or <c>null</c> when the key is unknown.
    /// Case-insensitive so a hand-typed URL still resolves.
    /// </summary>
    public static TopStreamedWindowDescriptor? Find(string? window)
    {
        if (string.IsNullOrWhiteSpace(window))
        {
            return null;
        }

        return All.FirstOrDefault(descriptor =>
            string.Equals(descriptor.Window, window, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="window"/> names one of the five playlists.</summary>
    public static bool IsKnown(string? window) => Find(window) is not null;
}
