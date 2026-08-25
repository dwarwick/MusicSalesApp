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

    /// <summary>
    /// Whether the user has at least one recorded stream of this song.
    ///
    /// This is the eligibility test for rating a song - see <see cref="LikeRequiresStreamException"/>.
    /// Streams recorded while logged out carry a null StreamerUserId and do not count towards the
    /// account that later signs in.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="songMetadataId">The ID of the song metadata record.</param>
    Task<bool> HasUserStreamedSongAsync(int userId, int songMetadataId);

    /// <summary>
    /// Bulk form of <see cref="HasUserStreamedSongAsync"/>: the subset of the given song IDs that this
    /// user has streamed. Used to tell clients which songs they may rate without a request per song.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="songMetadataIds">The song metadata IDs to test.</param>
    Task<HashSet<int>> GetUserStreamedSongIdsAsync(int userId, IEnumerable<int> songMetadataIds);
}
