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

            // Verify the user can add this song to the playlist
            if (!await CanAddSongToPlaylistAsync(userId, ownedSongId))
            {
                _logger.LogWarning("User {UserId} cannot add song {OwnedSongId} to playlist", userId, ownedSongId);
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

            // Check if user owns the song or has subscription access
            var ownedSong = await context.OwnedSongs
                .Include(os => os.SongMetadata)
                .FirstOrDefaultAsync(os => os.Id == ownedSongId && os.UserId == userId);

            if (ownedSong == null)
            {
                return false;
            }

            // Check if this is a valid song (not an album cover)
            // If we have metadata, check IsAlbumCover
            if (ownedSong.SongMetadata != null)
            {
                return !ownedSong.SongMetadata.IsAlbumCover;
            }
            
            // If no metadata, fall back to filename check
            // Album covers are typically .jpg, .jpeg, or .png, songs are .mp3
            var fileName = ownedSong.SongFileName.ToLowerInvariant();
            return fileName.EndsWith(".mp3");
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

            // Check if user has active subscription
            var hasActiveSubscription = await _subscriptionService.HasActiveSubscriptionAsync(userId);

            // Get all songs already in the playlist (as OwnedSong IDs)
            var playlistSongIds = await context.UserPlaylists
                .Where(up => up.PlaylistId == playlistId)
                .Select(up => up.OwnedSongId)
                .ToListAsync();

            // Get all owned songs by the user
            var ownedSongs = await context.OwnedSongs
                .Include(os => os.SongMetadata)
                .Where(os => os.UserId == userId)
                .Where(os => !playlistSongIds.Contains(os.Id))
                .ToListAsync();

            // Filter to exclude album covers
            var availableOwnedSongs = ownedSongs
                .Where(os => 
                {
                    // If we have metadata, use it to check
                    if (os.SongMetadata != null)
                    {
                        return !os.SongMetadata.IsAlbumCover;
                    }
                    
                    // If no metadata, fall back to filename check
                    return os.SongFileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            // If user has active subscription, include all songs from catalog
            if (hasActiveSubscription)
            {
                // Get all song metadata that are not album covers
                var allSongMetadata = await context.SongMetadata
                    .Where(sm => !sm.IsAlbumCover && sm.Mp3BlobPath != null)
                    .ToListAsync();

                // Get the metadata IDs of songs already owned by this user
                var ownedMetadataIds = new HashSet<int>(
                    availableOwnedSongs
                        .Where(os => os.SongMetadataId.HasValue)
                        .Select(os => os.SongMetadataId.Value));

                // Get metadata IDs of songs already in the playlist
                var playlistMetadataIds = await context.UserPlaylists
                    .Where(up => up.PlaylistId == playlistId)
                    .Include(up => up.OwnedSong)
                    .Where(up => up.OwnedSong.SongMetadataId.HasValue)
                    .Select(up => up.OwnedSong.SongMetadataId.Value)
                    .ToListAsync();

                var playlistMetadataIdSet = new HashSet<int>(playlistMetadataIds);

                // Load all existing OwnedSong records for this user upfront to avoid N+1 queries
                var existingUserOwnedSongs = await context.OwnedSongs
                    .Include(os => os.SongMetadata)
                    .Where(os => os.UserId == userId && os.SongMetadataId != null)
                    .ToListAsync();

                var existingOwnedSongsByMetadata = existingUserOwnedSongs
                    .Where(os => os.SongMetadataId.HasValue)
                    .ToDictionary(os => os.SongMetadataId.Value, os => os);

                // Collect new OwnedSong records to add in batch
                var newOwnedSongs = new List<OwnedSong>();

                // For each song metadata not already owned or in playlist, create/find an OwnedSong reference
                foreach (var metadata in allSongMetadata)
                {
                    // Skip if user already owns this song
                    if (ownedMetadataIds.Contains(metadata.Id))
                        continue;

                    // Skip if song is already in the playlist
                    if (playlistMetadataIdSet.Contains(metadata.Id))
                        continue;

                    // Check if we already have an OwnedSong record for this user and metadata
                    if (existingOwnedSongsByMetadata.TryGetValue(metadata.Id, out var userOwnedSong))
                    {
                        // Reuse existing OwnedSong record
                        availableOwnedSongs.Add(userOwnedSong);
                    }
                    else
                    {
                        // Create a "virtual" OwnedSong record for subscription access
                        // IMPORTANT: PayPalOrderId = null distinguishes subscription access from purchases
                        // - Purchased songs: PayPalOrderId is set to the PayPal order ID
                        // - Subscription songs: PayPalOrderId is null
                        // When subscription lapses, PlaylistCleanupService removes songs where PayPalOrderId is null
                        
                        // Defensive check (Mp3BlobPath is filtered at query level but extra safety here)
                        if (string.IsNullOrEmpty(metadata.Mp3BlobPath))
                            continue;
                            
                        var fileName = Path.GetFileName(metadata.Mp3BlobPath);
                        var newOwnedSong = new OwnedSong
                        {
                            UserId = userId,
                            SongFileName = fileName,
                            SongMetadataId = metadata.Id,
                            SongMetadata = metadata,
                            PurchasedAt = DateTime.UtcNow,
                            PayPalOrderId = null // Null = subscription access (cleaned up when subscription lapses)
                        };

                        newOwnedSongs.Add(newOwnedSong);
                        availableOwnedSongs.Add(newOwnedSong);
                    }
                }

                // Save all new OwnedSong records in a single batch operation
                if (newOwnedSongs.Any())
                {
                    context.OwnedSongs.AddRange(newOwnedSongs);
                    await context.SaveChangesAsync();
                }
            }

            return availableOwnedSongs;
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
                .Include(up => up.OwnedSong)
                .Where(up => up.PlaylistId == likedSongsPlaylist.Id)
                .ToListAsync();

            // Determine which songs need to be added
            var currentMetadataIds = currentPlaylistSongs
                .Where(up => up.OwnedSong?.SongMetadataId.HasValue == true)
                .Select(up => up.OwnedSong.SongMetadataId.Value)
                .ToHashSet();

            var songsToAdd = likedSongMetadataIds
                .Where(id => !currentMetadataIds.Contains(id))
                .ToList();

            // Determine which songs need to be removed
            var songsToRemove = currentPlaylistSongs
                .Where(up => up.OwnedSong?.SongMetadataId.HasValue == true && 
                            !likedSongMetadataIds.Contains(up.OwnedSong.SongMetadataId.Value))
                .ToList();

            // Check subscription status to determine if we can create virtual OwnedSong records
            var hasActiveSubscription = await _subscriptionService.HasActiveSubscriptionAsync(userId);

            // Get all existing OwnedSong records for this user to avoid creating duplicates
            var existingOwnedSongs = await context.OwnedSongs
                .Where(os => os.UserId == userId && os.SongMetadataId != null)
                .ToListAsync();

            var existingOwnedSongsByMetadata = existingOwnedSongs
                .Where(os => os.SongMetadataId.HasValue)
                .ToDictionary(os => os.SongMetadataId.Value, os => os);

            // Add new liked songs to the playlist
            foreach (var songMetadataId in songsToAdd)
            {
                OwnedSong ownedSong;

                // Check if user already owns this song
                if (existingOwnedSongsByMetadata.TryGetValue(songMetadataId, out var existingOwned))
                {
                    ownedSong = existingOwned;
                }
                else
                {
                    // User doesn't own the song - only create virtual ownership if they have an active subscription
                    if (hasActiveSubscription)
                    {
                        // Get the song metadata
                        var songMetadata = await context.SongMetadata.FindAsync(songMetadataId);
                        if (songMetadata == null || string.IsNullOrEmpty(songMetadata.Mp3BlobPath))
                        {
                            _logger.LogWarning("Cannot add song {SongMetadataId} to Liked Songs - metadata not found or no MP3", songMetadataId);
                            continue;
                        }

                        // Create a virtual OwnedSong record (subscription access)
                        var fileName = Path.GetFileName(songMetadata.Mp3BlobPath);
                        ownedSong = new OwnedSong
                        {
                            UserId = userId,
                            SongFileName = fileName,
                            SongMetadataId = songMetadataId,
                            PurchasedAt = DateTime.UtcNow,
                            PayPalOrderId = null // Null = subscription access
                        };

                        context.OwnedSongs.Add(ownedSong);
                        await context.SaveChangesAsync(); // Save to get the ID

                        // Cache it for future iterations
                        existingOwnedSongsByMetadata[songMetadataId] = ownedSong;
                    }
                    else
                    {
                        // User doesn't own the song and has no subscription - skip it
                        _logger.LogInformation("Skipping song {SongMetadataId} for Liked Songs - user doesn't own it and has no subscription", songMetadataId);
                        continue;
                    }
                }

                // Add to the playlist if not already there
                var existingPlaylistEntry = await context.UserPlaylists
                    .FirstOrDefaultAsync(up => up.PlaylistId == likedSongsPlaylist.Id && up.OwnedSongId == ownedSong.Id);

                if (existingPlaylistEntry == null)
                {
                    var userPlaylist = new UserPlaylist
                    {
                        UserId = userId,
                        PlaylistId = likedSongsPlaylist.Id,
                        OwnedSongId = ownedSong.Id,
                        AddedAt = DateTime.UtcNow
                    };

                    context.UserPlaylists.Add(userPlaylist);
                }
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
