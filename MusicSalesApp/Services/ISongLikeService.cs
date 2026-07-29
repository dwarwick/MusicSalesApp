namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing song likes and dislikes
/// </summary>
public interface ISongLikeService
{
    /// <summary>
    /// Get the like and dislike counts for a song
    /// </summary>
    /// <param name="songMetadataId">The song metadata ID</param>
    /// <returns>Tuple of (likeCount, dislikeCount)</returns>
    Task<(int likeCount, int dislikeCount)> GetLikeCountsAsync(int songMetadataId);

    /// <summary>
    /// Get the user's like status for a song
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="songMetadataId">The song metadata ID</param>
    /// <returns>True if liked, False if disliked, null if no preference</returns>
    Task<bool?> GetUserLikeStatusAsync(int userId, int songMetadataId);

    /// <summary>
    /// Toggle a like for a song. If the user has already liked the song, remove the like.
    /// If the user has disliked the song, change it to a like.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="songMetadataId">The song metadata ID</param>
    /// <returns>True if the song is now liked, false if the like was removed</returns>
    Task<bool> ToggleLikeAsync(int userId, int songMetadataId);

    /// <summary>
    /// Toggle a dislike for a song. If the user has already disliked the song, remove the dislike.
    /// If the user has liked the song, change it to a dislike.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="songMetadataId">The song metadata ID</param>
    /// <returns>True if the song is now disliked, false if the dislike was removed</returns>
    Task<bool> ToggleDislikeAsync(int userId, int songMetadataId);

    /// <summary>
    /// Idempotently set the user's opinion of a song, replacing whatever it was before.
    /// Unlike <see cref="ToggleLikeAsync"/>/<see cref="ToggleDislikeAsync"/> the outcome depends only on
    /// <paramref name="state"/>, never on the current row, so a client may safely replay the same call.
    /// This is what the mobile app uses to drain its offline like/dislike queue on reconnect.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="songMetadataId">The song metadata ID</param>
    /// <param name="state">True to like, false to dislike, null to clear any opinion</param>
    /// <returns>The resulting like status (echoes <paramref name="state"/>)</returns>
    Task<bool?> SetLikeStateAsync(int userId, int songMetadataId, bool? state);

    /// <summary>
    /// Get all songs that a user has liked (not disliked)
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <returns>List of SongMetadata IDs that the user has liked</returns>
    Task<List<int>> GetUserLikedSongIdsAsync(int userId);

    /// <summary>
    /// Get like counts for multiple songs in a single query
    /// </summary>
    /// <param name="songMetadataIds">The song metadata IDs</param>
    /// <returns>Dictionary mapping songMetadataId to likeCount</returns>
    Task<Dictionary<int, int>> GetBulkLikeCountsAsync(IEnumerable<int> songMetadataIds);

    /// <summary>
    /// Get both like and dislike counts for multiple songs in a single query.
    /// More efficient than calling GetLikeCountsAsync for each song individually.
    /// </summary>
    /// <param name="songMetadataIds">The song metadata IDs</param>
    /// <returns>Dictionary mapping songMetadataId to (likeCount, dislikeCount)</returns>
    Task<Dictionary<int, (int likeCount, int dislikeCount)>> GetBulkLikeDislikeCountsAsync(IEnumerable<int> songMetadataIds);

    /// <summary>
    /// Get a user's like/dislike status for multiple songs in a single query.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="songMetadataIds">The song metadata IDs</param>
    /// <returns>Dictionary mapping songMetadataId to like status (true=liked, false=disliked, null=none)</returns>
    Task<Dictionary<int, bool?>> GetBulkUserLikeStatusAsync(int userId, IEnumerable<int> songMetadataIds);
}
