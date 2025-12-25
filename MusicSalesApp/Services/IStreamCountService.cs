namespace MusicSalesApp.Services;

/// <summary>
/// Service for tracking and managing song stream counts.
/// A song counts as streamed when it has been played for at least 30 continuous seconds
/// without seeking.
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
    /// </summary>
    /// <param name="songMetadataId">The ID of the song metadata record.</param>
    /// <returns>The new stream count after incrementing.</returns>
    Task<int> IncrementStreamCountAsync(int songMetadataId);

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
