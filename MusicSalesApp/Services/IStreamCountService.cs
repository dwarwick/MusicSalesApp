namespace MusicSalesApp.Services;

/// <summary>
/// Service for tracking and managing song stream counts.
/// A song counts as streamed when it has been played for at least the creator's configured
/// continuous seconds without seeking.
/// </summary>
public interface IStreamCountService
{
    /// <summary>
    /// Event fired when a song's stream count is updated.
    /// The int parameter is the SongMetadataId that was updated.
    /// </summary>
    event Action<int, int> OnStreamCountUpdated;

    /// <summary>
    /// Increments the stream count for a song atomically.
    /// Also creates a SongStream record for auditing.
    /// When the streamer is the song's creator, neither the count nor a SongStream record is
    /// written (creators do not generate paid stream counts for their own songs).
    /// Admin streams count like any other listener's; admins are exempt from the featured-song
    /// free-stream cap because they get full playback without a subscription.
    /// </summary>
    /// <param name="songMetadataId">The ID of the song metadata record.</param>
    /// <param name="streamerUserId">The ID of the user who streamed the song, or null if unauthenticated.</param>
    /// <param name="isAdmin">Whether the streamer is an admin (treated as fully entitled to playback).</param>
    /// <returns>The new stream count after processing.</returns>
    Task<int> IncrementStreamCountAsync(int songMetadataId, int? streamerUserId = null, bool isAdmin = false);

    /// <summary>
    /// Gets the current stream count for a song.
    /// </summary>
    /// <param name="songMetadataId">The ID of the song metadata record.</param>
    /// <returns>The current stream count, or 0 if not found.</returns>
    Task<int> GetStreamCountAsync(int songMetadataId);

    /// <summary>
    /// Notifies subscribers that a stream count has been updated.
    /// </summary>
    /// <param name="songMetadataId">The ID of the song metadata record.</param>
    /// <param name="newCount">The new stream count.</param>
    void NotifyStreamCountUpdated(int songMetadataId, int newCount);
}
