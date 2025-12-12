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

    public PlaylistService(IDbContextFactory<AppDbContext> contextFactory, ILogger<PlaylistService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
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
                .Include(up => up.OwnedSong)
                    .ThenInclude(os => os.SongMetadata)
                .Where(up => up.PlaylistId == playlistId)
                .OrderBy(up => up.AddedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting songs for playlist {PlaylistId}", playlistId);
            throw;
        }
    }

    public async Task<bool> AddSongToPlaylistAsync(int userId, int playlistId, int ownedSongId)
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

            // Verify the user owns the song and it's valid for playlists
            if (!await CanAddSongToPlaylistAsync(userId, ownedSongId))
            {
                _logger.LogWarning("User {UserId} doesn't own song {OwnedSongId} or it's not valid for playlists", userId, ownedSongId);
                return false;
            }

            // Check if song is already in playlist
            var existingSong = await context.UserPlaylists
                .FirstOrDefaultAsync(up => up.PlaylistId == playlistId && up.OwnedSongId == ownedSongId);

            if (existingSong != null)
            {
                _logger.LogWarning("Song {OwnedSongId} already in playlist {PlaylistId}", ownedSongId, playlistId);
                return false;
            }

            // Add song to playlist
            var userPlaylist = new UserPlaylist
            {
                UserId = userId,
                PlaylistId = playlistId,
                OwnedSongId = ownedSongId,
                AddedAt = DateTime.UtcNow
            };

            context.UserPlaylists.Add(userPlaylist);
            await context.SaveChangesAsync();

            _logger.LogInformation("Added song {OwnedSongId} to playlist {PlaylistId}", ownedSongId, playlistId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding song {OwnedSongId} to playlist {PlaylistId}", ownedSongId, playlistId);
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

    public async Task<bool> CanAddSongToPlaylistAsync(int userId, int ownedSongId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Check if user owns the song
            var ownedSong = await context.OwnedSongs
                .Include(os => os.SongMetadata)
                .FirstOrDefaultAsync(os => os.Id == ownedSongId && os.UserId == userId);

            if (ownedSong == null)
            {
                return false;
            }

            // Check if SongMetadata exists and IsAlbumCover is false
            if (ownedSong.SongMetadata == null || ownedSong.SongMetadata.IsAlbumCover)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if user {UserId} can add song {OwnedSongId} to playlist", userId, ownedSongId);
            throw;
        }
    }

    public async Task<List<OwnedSong>> GetAvailableSongsForPlaylistAsync(int userId, int playlistId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Get all songs in the playlist
            var playlistSongIds = await context.UserPlaylists
                .Where(up => up.PlaylistId == playlistId)
                .Select(up => up.OwnedSongId)
                .ToListAsync();

            // Get all owned songs by the user that are not album covers and not already in the playlist
            var availableSongs = await context.OwnedSongs
                .Include(os => os.SongMetadata)
                .Where(os => os.UserId == userId)
                .Where(os => os.SongMetadata != null && !os.SongMetadata.IsAlbumCover)
                .Where(os => !playlistSongIds.Contains(os.Id))
                .ToListAsync();

            return availableSongs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available songs for playlist {PlaylistId} and user {UserId}", playlistId, userId);
            throw;
        }
    }
}
