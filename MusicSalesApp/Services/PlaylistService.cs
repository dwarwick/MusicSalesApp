using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing playlists and playlist songs
/// </summary>
public class PlaylistService : IPlaylistService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<PlaylistService> _logger;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ISongLikeService _songLikeService;

    public PlaylistService(
        IDbContextFactory<AppDbContext> contextFactory, 
        ILogger<PlaylistService> logger,
        ISubscriptionService subscriptionService,
        ISongLikeService songLikeService)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _subscriptionService = subscriptionService;
        _songLikeService = songLikeService;
    }

    public async Task<List<Playlist>> GetUserPlaylistsAsync(int userId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.Playlists
                .Where(p => p.UserId == userId)
                .OrderBy(p => p.PlaylistName)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting playlists for user {UserId}", userId);
            throw;
        }
    }

    public async Task<Playlist> GetPlaylistByIdAsync(int playlistId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.Playlists
                .Include(p => p.UserPlaylists)
                .FirstOrDefaultAsync(p => p.Id == playlistId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting playlist {PlaylistId}", playlistId);
            throw;
        }
    }

    public async Task<Playlist> CreatePlaylistAsync(int userId, string playlistName)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            var playlist = new Playlist
            {
                UserId = userId,
                PlaylistName = playlistName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            context.Playlists.Add(playlist);
            await context.SaveChangesAsync();

            _logger.LogInformation("Created playlist {PlaylistName} for user {UserId}", playlistName, userId);
            return playlist;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating playlist for user {UserId}", userId);
            throw;
        }
    }

    public async Task<bool> UpdatePlaylistAsync(int playlistId, int userId, string playlistName)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            var playlist = await context.Playlists
                .FirstOrDefaultAsync(p => p.Id == playlistId && p.UserId == userId);

            if (playlist == null)
            {
                _logger.LogWarning("Playlist {PlaylistId} not found or user {UserId} doesn't own it", playlistId, userId);
                return false;
            }

            // Prevent editing system-generated playlists
            if (playlist.IsSystemGenerated)
            {
                _logger.LogWarning("Cannot update system-generated playlist {PlaylistId}", playlistId);
                return false;
            }

            playlist.PlaylistName = playlistName;
            playlist.UpdatedAt = DateTime.UtcNow;

            context.Playlists.Update(playlist);
            await context.SaveChangesAsync();

            _logger.LogInformation("Updated playlist {PlaylistId} for user {UserId}", playlistId, userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating playlist {PlaylistId}", playlistId);
            throw;
        }
    }

    public async Task<bool> DeletePlaylistAsync(int playlistId, int userId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            var playlist = await context.Playlists
                .FirstOrDefaultAsync(p => p.Id == playlistId && p.UserId == userId);

            if (playlist == null)
            {
                _logger.LogWarning("Playlist {PlaylistId} not found or user {UserId} doesn't own it", playlistId, userId);
                return false;
            }

            // Prevent deleting system-generated playlists
            if (playlist.IsSystemGenerated)
            {
                _logger.LogWarning("Cannot delete system-generated playlist {PlaylistId}", playlistId);
                return false;
            }

            context.Playlists.Remove(playlist);
            await context.SaveChangesAsync();

            _logger.LogInformation("Deleted playlist {PlaylistId} for user {UserId}", playlistId, userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting playlist {PlaylistId}", playlistId);
            throw;
        }
    }

    public async Task<List<UserPlaylist>> GetPlaylistSongsAsync(int playlistId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.UserPlaylists
                .Include(up => up.SongMetadata)
                .Where(up => up.PlaylistId == playlistId)
                .Where(up => up.SongMetadata != null && up.SongMetadata.IsEnabled) // Filter out disabled songs
                .OrderBy(up => up.AddedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting songs for playlist {PlaylistId}", playlistId);
            throw;
        }
    }

    public async Task<bool> AddSongToPlaylistAsync(int userId, int playlistId, int songMetadataId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Verify the playlist belongs to the user
            var playlist = await context.Playlists
                .FirstOrDefaultAsync(p => p.Id == playlistId && p.UserId == userId);

            if (playlist == null)
            {
                _logger.LogWarning("Playlist {PlaylistId} not found or user {UserId} doesn't own it", playlistId, userId);
                return false;
            }

            // Verify the song can be added (not an album cover)
            if (!await CanAddSongToPlaylistAsync(songMetadataId))
            {
                _logger.LogWarning("Song {SongMetadataId} cannot be added to playlist", songMetadataId);
                return false;
            }

            // Check if song is already in playlist
            var existingSong = await context.UserPlaylists
                .FirstOrDefaultAsync(up => up.PlaylistId == playlistId && up.SongMetadataId == songMetadataId);

            if (existingSong != null)
            {
                _logger.LogWarning("Song {SongMetadataId} already in playlist {PlaylistId}", songMetadataId, playlistId);
                return false;
            }

            // Add song to playlist
            var userPlaylist = new UserPlaylist
            {
                UserId = userId,
                PlaylistId = playlistId,
                SongMetadataId = songMetadataId,
                AddedAt = DateTime.UtcNow
            };

            context.UserPlaylists.Add(userPlaylist);
            await context.SaveChangesAsync();

            _logger.LogInformation("Added song {SongMetadataId} to playlist {PlaylistId}", songMetadataId, playlistId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding song {SongMetadataId} to playlist {PlaylistId}", songMetadataId, playlistId);
            throw;
        }
    }

    public async Task<bool> RemoveSongFromPlaylistAsync(int playlistId, int userPlaylistId, int userId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Verify the playlist belongs to the user and the song is in the playlist
            var userPlaylist = await context.UserPlaylists
                .Include(up => up.Playlist)
                .FirstOrDefaultAsync(up => up.Id == userPlaylistId && 
                                          up.PlaylistId == playlistId && 
                                          up.Playlist.UserId == userId);

            if (userPlaylist == null)
            {
                _logger.LogWarning("UserPlaylist {UserPlaylistId} not found in playlist {PlaylistId} for user {UserId}", 
                    userPlaylistId, playlistId, userId);
                return false;
            }

            context.UserPlaylists.Remove(userPlaylist);
            await context.SaveChangesAsync();

            _logger.LogInformation("Removed song from playlist {PlaylistId}", playlistId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing song from playlist {PlaylistId}", playlistId);
            throw;
        }
    }

    public async Task<bool> CanAddSongToPlaylistAsync(int songMetadataId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var metadata = await context.SongMetadata
                .FirstOrDefaultAsync(sm => sm.Id == songMetadataId);

            if (metadata == null)
            {
                return false;
            }

            // Check if this is a valid song (not an album cover, is enabled, and has MP3)
            return !metadata.IsAlbumCover && 
                   metadata.IsEnabled && 
                   !string.IsNullOrEmpty(metadata.Mp3BlobPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if song {SongMetadataId} can be added to playlist", songMetadataId);
            throw;
        }
    }

    public async Task<List<SongMetadata>> GetAvailableSongsForPlaylistAsync(int userId, int playlistId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Check if user has active subscription
            var hasActiveSubscription = await _subscriptionService.HasActiveSubscriptionAsync(userId);

            if (!hasActiveSubscription)
            {
                // Users without subscription cannot add songs to playlists
                _logger.LogInformation("User {UserId} does not have an active subscription", userId);
                return new List<SongMetadata>();
            }

            // Get song IDs already in the playlist
            var playlistSongIds = await context.UserPlaylists
                .Where(up => up.PlaylistId == playlistId)
                .Select(up => up.SongMetadataId)
                .ToListAsync();

            // Get all active and enabled songs that are not album covers and not already in the playlist
            var availableSongs = await context.SongMetadata
                .Where(sm => sm.IsActive && 
                             sm.IsEnabled && // Filter out disabled songs
                             !sm.IsAlbumCover && 
                             !string.IsNullOrEmpty(sm.Mp3BlobPath) &&
                             !playlistSongIds.Contains(sm.Id))
                .OrderBy(sm => sm.AlbumName)
                .ThenBy(sm => sm.TrackNumber)
                .ThenBy(sm => sm.SongTitle)
                .ToListAsync();

            return availableSongs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available songs for playlist {PlaylistId} and user {UserId}", playlistId, userId);
            throw;
        }
    }

    public async Task<Playlist> GetOrCreateLikedSongsPlaylistAsync(int userId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Check if the Liked Songs playlist already exists for this user
            var likedSongsPlaylist = await context.Playlists
                .FirstOrDefaultAsync(p => p.UserId == userId && p.IsSystemGenerated && p.PlaylistName == "Liked Songs");

            if (likedSongsPlaylist == null)
            {
                // Create the Liked Songs playlist
                likedSongsPlaylist = new Playlist
                {
                    UserId = userId,
                    PlaylistName = "Liked Songs",
                    IsSystemGenerated = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                context.Playlists.Add(likedSongsPlaylist);
                await context.SaveChangesAsync();

                _logger.LogInformation("Created Liked Songs playlist for user {UserId}", userId);
            }

            return likedSongsPlaylist;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting or creating Liked Songs playlist for user {UserId}", userId);
            throw;
        }
    }

    public async Task SyncLikedSongsPlaylistAsync(int userId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Get or create the Liked Songs playlist
            var likedSongsPlaylist = await GetOrCreateLikedSongsPlaylistAsync(userId);

            // Get all liked song metadata IDs
            var likedSongMetadataIds = await _songLikeService.GetUserLikedSongIdsAsync(userId);

            // Get current songs in the Liked Songs playlist
            var currentPlaylistSongs = await context.UserPlaylists
                .Where(up => up.PlaylistId == likedSongsPlaylist.Id)
                .ToListAsync();

            // Determine which songs need to be added
            var currentMetadataIds = currentPlaylistSongs
                .Select(up => up.SongMetadataId)
                .ToHashSet();

            var songsToAdd = likedSongMetadataIds
                .Where(id => !currentMetadataIds.Contains(id))
                .ToList();

            // Determine which songs need to be removed
            var songsToRemove = currentPlaylistSongs
                .Where(up => !likedSongMetadataIds.Contains(up.SongMetadataId))
                .ToList();

            // Add new liked songs to the playlist
            foreach (var songMetadataId in songsToAdd)
            {
                // Verify the song exists and is valid
                var songMetadata = await context.SongMetadata.FindAsync(songMetadataId);
                if (songMetadata == null || string.IsNullOrEmpty(songMetadata.Mp3BlobPath) || songMetadata.IsAlbumCover)
                {
                    _logger.LogWarning("Cannot add song {SongMetadataId} to Liked Songs - invalid metadata", songMetadataId);
                    continue;
                }

                var userPlaylist = new UserPlaylist
                {
                    UserId = userId,
                    PlaylistId = likedSongsPlaylist.Id,
                    SongMetadataId = songMetadataId,
                    AddedAt = DateTime.UtcNow
                };

                context.UserPlaylists.Add(userPlaylist);
            }

            // Remove unliked songs from the playlist
            if (songsToRemove.Any())
            {
                context.UserPlaylists.RemoveRange(songsToRemove);
            }

            await context.SaveChangesAsync();

            _logger.LogInformation("Synced Liked Songs playlist for user {UserId}: added {AddCount}, removed {RemoveCount}", 
                userId, songsToAdd.Count, songsToRemove.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing Liked Songs playlist for user {UserId}", userId);
            throw;
        }
    }
}
