using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Builds and serves the five global "most streamed" playlists.
///
/// <para>
/// Unlike <see cref="IRecommendationService"/> these are not per-user: every listener, signed in or
/// not, sees the same five lists. They are rebuilt by the nightly <c>generate-recommendations</c>
/// Hangfire job.
/// </para>
/// </summary>
public interface ITopStreamedPlaylistService
{
    /// <summary>
    /// Rebuilds all five playlists. Each window is replaced independently, so one window failing does
    /// not leave the others half-written.
    /// </summary>
    Task GenerateAllAsync();

    /// <summary>
    /// The songs in one playlist, in rank order (most streamed first).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-applies the catalogue visibility filter, so a song disabled since the playlist was generated
    /// disappears rather than being served as an unplayable row. Returns an empty list for an unknown
    /// window key.
    /// </para>
    /// <para>
    /// For the four rolling windows the period stream count on each entry is <b>recounted from the
    /// moment of the read</b>, so plays that happened since the nightly job - including ones made
    /// somewhere else in the app - are already reflected when the playlist is opened. Membership and
    /// rank order stay as generated.
    /// </para>
    /// </remarks>
    Task<List<TopStreamedPlaylistEntry>> GetAsync(string window);

    /// <summary>
    /// When the playlists were last rebuilt, or <c>null</c> if they never have been.
    /// </summary>
    /// <remarks>
    /// Surfaced to listeners because rank order is up to a day old while the counts beside it are
    /// live, so the two can disagree slightly. Saying when the ranking was taken is what makes that
    /// legible rather than looking like a sorting bug.
    /// </remarks>
    Task<DateTime?> GetLastGeneratedAtAsync();

    /// <summary>
    /// How many playable songs each playlist currently holds, keyed by window.
    ///
    /// <para>
    /// One query for all five, so a page showing every playlist as a tile does not pay five round-trips.
    /// A window with no entries is absent from the dictionary rather than present with a zero - the
    /// callers hide empty playlists, and this keeps "hide it" a single <c>TryGetValue</c> miss.
    /// </para>
    /// </summary>
    Task<Dictionary<string, int>> GetCountsAsync();
}
