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
}
