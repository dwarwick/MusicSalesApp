using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing playlists
/// </summary>
public interface IPlaylistService
{
    /// <summary>
    /// Get all playlists for a specific user
    /// </summary>
    Task<List<Playlist>> GetUserPlaylistsAsync(int userId);

    /// <summary>
    /// Get a specific playlist by Id
    /// </summary>
    Task<Playlist> GetPlaylistByIdAsync(int playlistId);

    /// <summary>
    /// Create a new playlist
    /// </summary>
    Task<Playlist> CreatePlaylistAsync(int userId, string playlistName);

    /// <summary>
    /// Update a playlist name
    /// </summary>
    Task<bool> UpdatePlaylistAsync(int playlistId, int userId, string playlistName);

    /// <summary>
    /// Delete a playlist
    /// </summary>
    Task<bool> DeletePlaylistAsync(int playlistId, int userId);

    /// <summary>
    /// Get all songs in a playlist
    /// </summary>
    Task<List<UserPlaylist>> GetPlaylistSongsAsync(int playlistId);

    /// <summary>
    /// Add a song to a playlist (only if user owns the song and it's not an album cover)
    /// </summary>
    Task<bool> AddSongToPlaylistAsync(int userId, int playlistId, int ownedSongId);

    /// <summary>
    /// Remove a song from a playlist
    /// </summary>
    Task<bool> RemoveSongFromPlaylistAsync(int playlistId, int userPlaylistId, int userId);

    /// <summary>
    /// Check if user owns a specific song and if it's valid for playlists (IsAlbumCover = false)
    /// </summary>
    Task<bool> CanAddSongToPlaylistAsync(int userId, int ownedSongId);

    /// <summary>
    /// Get all songs owned by a user that are available to add to a playlist
    /// Filters out songs already in the playlist and album covers
    /// </summary>
    Task<List<OwnedSong>> GetAvailableSongsForPlaylistAsync(int userId, int playlistId);

    /// <summary>
    /// Get or create the "Liked Songs" system playlist for a user
    /// </summary>
    Task<Playlist> GetOrCreateLikedSongsPlaylistAsync(int userId);

    /// <summary>
    /// Sync the Liked Songs playlist with the user's current liked songs
    /// Adds newly liked songs and removes unliked songs
    /// </summary>
    Task SyncLikedSongsPlaylistAsync(int userId);
}
