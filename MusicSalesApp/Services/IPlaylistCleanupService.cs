namespace MusicSalesApp.Services;

/// <summary>
/// Service for cleaning up playlist songs for users with lapsed subscriptions
/// </summary>
public interface IPlaylistCleanupService
{
    /// <summary>
    /// Removes songs from playlists for users whose subscriptions have lapsed
    /// and they don't own the songs outright
    /// </summary>
    /// <returns>Number of songs removed</returns>
    Task<int> RemoveNonOwnedSongsFromLapsedSubscriptionsAsync();
}
