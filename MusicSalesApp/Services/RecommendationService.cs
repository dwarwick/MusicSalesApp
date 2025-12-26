using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using Supabase;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating song recommendations using collaborative filtering via Supabase + pgvector
/// </summary>
public class RecommendationService : IRecommendationService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<RecommendationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private const int MaxRecommendations = 20;

    public RecommendationService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<RecommendationService> logger,
        IConfiguration configuration)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _configuration = configuration;
        _supabaseUrl = configuration["Supabase:SUPABASE_URL"] ?? string.Empty;
        _supabaseKey = configuration["Supabase:SUPABASE_KEY"] ?? string.Empty;
    }

    /// <inheritdoc/>
    public async Task<List<RecommendedPlaylist>> GetRecommendedPlaylistAsync(int userId)
    {
        try
        {
            // In DEBUG mode, always generate fresh recommendations
#if DEBUG
            return await GenerateRecommendationsAsync(userId);
#else
            // In RELEASE mode, check if fresh recommendations exist (within 24 hours)
            if (await HasFreshRecommendationsAsync(userId))
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                return await context.RecommendedPlaylists
                    .Include(rp => rp.SongMetadata)
                    .Where(rp => rp.UserId == userId)
                    .OrderBy(rp => rp.DisplayOrder)
                    .ToListAsync();
            }

            return await GenerateRecommendationsAsync(userId);
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recommended playlist for user {UserId}", userId);
            return new List<RecommendedPlaylist>();
        }
    }

    /// <inheritdoc/>
    public async Task<List<RecommendedPlaylist>> GenerateRecommendationsAsync(int userId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Clear existing recommendations for this user
            var existingRecommendations = await context.RecommendedPlaylists
                .Where(rp => rp.UserId == userId)
                .ToListAsync();
            context.RecommendedPlaylists.RemoveRange(existingRecommendations);

            // Get the user's liked songs
            var userLikes = await context.SongLikes
                .Where(sl => sl.UserId == userId && sl.IsLike)
                .Select(sl => sl.SongMetadataId)
                .ToListAsync();

            // Get the user's disliked songs (to exclude them)
            var userDislikes = await context.SongLikes
                .Where(sl => sl.UserId == userId && !sl.IsLike)
                .Select(sl => sl.SongMetadataId)
                .ToListAsync();

            List<(int SongId, double Score)> recommendedSongs;

            // Try Supabase-based recommendations first if configured
            if (!string.IsNullOrEmpty(_supabaseUrl) && !string.IsNullOrEmpty(_supabaseKey) && 
                _supabaseUrl != "__REPLACE_WITH_SUPABASE_URL__" && _supabaseKey != "__REPLACE_WITH_SUPABASE_KEY__")
            {
                recommendedSongs = await GetSupabaseRecommendationsAsync(userId, userLikes, userDislikes);
            }
            else
            {
                // Fallback to local collaborative filtering
                recommendedSongs = await GetLocalRecommendationsAsync(context, userId, userLikes, userDislikes);
            }

            // Create RecommendedPlaylist entries
            var recommendations = new List<RecommendedPlaylist>();
            var displayOrder = 1;

            foreach (var (songId, score) in recommendedSongs.Take(MaxRecommendations))
            {
                recommendations.Add(new RecommendedPlaylist
                {
                    UserId = userId,
                    SongMetadataId = songId,
                    DisplayOrder = displayOrder++,
                    GeneratedAt = DateTime.UtcNow,
                    Score = score
                });
            }

            if (recommendations.Any())
            {
                context.RecommendedPlaylists.AddRange(recommendations);
                await context.SaveChangesAsync();

                // Reload with navigation properties
                return await context.RecommendedPlaylists
                    .Include(rp => rp.SongMetadata)
                    .Where(rp => rp.UserId == userId)
                    .OrderBy(rp => rp.DisplayOrder)
                    .ToListAsync();
            }

            return recommendations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating recommendations for user {UserId}", userId);
            return new List<RecommendedPlaylist>();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> HasFreshRecommendationsAsync(int userId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            var cutoff = DateTime.UtcNow.AddHours(-24);
            return await context.RecommendedPlaylists
                .AnyAsync(rp => rp.UserId == userId && rp.GeneratedAt > cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for fresh recommendations for user {UserId}", userId);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task SyncLikesToSupabaseAsync()
    {
        if (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabaseKey) ||
            _supabaseUrl == "__REPLACE_WITH_SUPABASE_URL__" || _supabaseKey == "__REPLACE_WITH_SUPABASE_KEY__")
        {
            _logger.LogWarning("Supabase is not configured. Skipping sync.");
            return;
        }

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Get all likes from SQL Server
            var allLikes = await context.SongLikes
                .Select(sl => new
                {
                    sl.UserId,
                    sl.SongMetadataId,
                    sl.IsLike,
                    sl.CreatedAt,
                    sl.UpdatedAt
                })
                .ToListAsync();

            // Create Supabase client
            var options = new SupabaseOptions
            {
                AutoRefreshToken = true,
                AutoConnectRealtime = false
            };
            var supabase = new Client(_supabaseUrl, _supabaseKey, options);
            await supabase.InitializeAsync();

            // Sync likes to Supabase song_likes table
            // Note: This assumes a matching table structure exists in Supabase
            foreach (var like in allLikes)
            {
                try
                {
                    await supabase.From<SupabaseSongLike>()
                        .Upsert(new SupabaseSongLike
                        {
                            UserId = like.UserId,
                            SongMetadataId = like.SongMetadataId,
                            IsLike = like.IsLike,
                            CreatedAt = like.CreatedAt,
                            UpdatedAt = like.UpdatedAt
                        });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync like for user {UserId}, song {SongId}", 
                        like.UserId, like.SongMetadataId);
                }
            }

            _logger.LogInformation("Successfully synced {Count} likes to Supabase", allLikes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing likes to Supabase");
            throw;
        }
    }

    /// <summary>
    /// Get recommendations using Supabase pgvector for similarity search
    /// </summary>
    private async Task<List<(int SongId, double Score)>> GetSupabaseRecommendationsAsync(
        int userId, 
        List<int> userLikes, 
        List<int> userDislikes)
    {
        try
        {
            var options = new SupabaseOptions
            {
                AutoRefreshToken = true,
                AutoConnectRealtime = false
            };
            var supabase = new Client(_supabaseUrl, _supabaseKey, options);
            await supabase.InitializeAsync();

            // Call a Supabase RPC function for collaborative filtering recommendations
            // This assumes a stored procedure "get_recommendations" exists in Supabase
            var result = await supabase.Rpc("get_recommendations", new Dictionary<string, object>
            {
                { "p_user_id", userId },
                { "p_limit", MaxRecommendations },
                { "p_exclude_songs", userDislikes }
            });

            if (result.Content == null)
            {
                return new List<(int SongId, double Score)>();
            }

            // Parse result - assuming the RPC returns a list of {song_id, score}
            var recommendations = System.Text.Json.JsonSerializer.Deserialize<List<SupabaseRecommendation>>(
                result.Content, 
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return recommendations?
                .Select(r => (r.SongId, r.Score))
                .ToList() ?? new List<(int SongId, double Score)>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Supabase recommendations failed, falling back to local recommendations");
            
            // Fallback to local recommendations
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await GetLocalRecommendationsAsync(context, userId, userLikes, userDislikes);
        }
    }

    /// <summary>
    /// Get recommendations using local collaborative filtering algorithm
    /// "Users who liked X also liked Y"
    /// </summary>
    private async Task<List<(int SongId, double Score)>> GetLocalRecommendationsAsync(
        AppDbContext context,
        int userId,
        List<int> userLikes,
        List<int> userDislikes)
    {
        // If user has no likes, recommend popular songs
        if (!userLikes.Any())
        {
            return await GetPopularSongsAsync(context, userDislikes);
        }

        // Find users who liked the same songs as this user (similar users)
        var similarUserIds = await context.SongLikes
            .Where(sl => userLikes.Contains(sl.SongMetadataId) && sl.IsLike && sl.UserId != userId)
            .Select(sl => sl.UserId)
            .Distinct()
            .ToListAsync();

        if (!similarUserIds.Any())
        {
            // No similar users found, fall back to popular songs
            return await GetPopularSongsAsync(context, userDislikes);
        }

        // Get songs that similar users liked, that this user hasn't rated yet
        var songScores = await context.SongLikes
            .Where(sl => similarUserIds.Contains(sl.UserId) && sl.IsLike)
            .Where(sl => !userLikes.Contains(sl.SongMetadataId)) // Not already liked by user
            .Where(sl => !userDislikes.Contains(sl.SongMetadataId)) // Not disliked by user
            .GroupBy(sl => sl.SongMetadataId)
            .Select(g => new
            {
                SongId = g.Key,
                Score = (double)g.Count() // Score based on how many similar users liked it
            })
            .OrderByDescending(x => x.Score)
            .Take(MaxRecommendations)
            .ToListAsync();

        // Verify these songs exist and are playable (have Mp3BlobPath)
        var validSongIds = await context.SongMetadata
            .Where(sm => songScores.Select(s => s.SongId).Contains(sm.Id))
            .Where(sm => !sm.IsAlbumCover && sm.Mp3BlobPath != null)
            .Select(sm => sm.Id)
            .ToListAsync();

        var results = songScores
            .Where(s => validSongIds.Contains(s.SongId))
            .Select(s => (s.SongId, s.Score))
            .ToList();

        // If we don't have enough recommendations, supplement with popular songs
        if (results.Count < MaxRecommendations)
        {
            var existingSongIds = results.Select(r => r.SongId).ToHashSet();
            var excludeIds = userLikes.Concat(userDislikes).Concat(existingSongIds).ToList();
            
            var popularSongs = await GetPopularSongsAsync(context, excludeIds, MaxRecommendations - results.Count);
            results.AddRange(popularSongs);
        }

        return results;
    }

    /// <summary>
    /// Get popular songs based on like count, stream count, or both
    /// </summary>
    private async Task<List<(int SongId, double Score)>> GetPopularSongsAsync(
        AppDbContext context, 
        List<int> excludeSongIds, 
        int limit = 20)
    {
        // Get songs with most likes that aren't in the exclude list
        var popularByLikes = await context.SongLikes
            .Where(sl => sl.IsLike)
            .Where(sl => !excludeSongIds.Contains(sl.SongMetadataId))
            .GroupBy(sl => sl.SongMetadataId)
            .Select(g => new
            {
                SongId = g.Key,
                LikeCount = g.Count()
            })
            .OrderByDescending(x => x.LikeCount)
            .Take(limit * 2) // Get extra to account for filtering
            .ToListAsync();

        // Verify these songs exist and are playable
        var validSongs = await context.SongMetadata
            .Where(sm => popularByLikes.Select(p => p.SongId).Contains(sm.Id))
            .Where(sm => !sm.IsAlbumCover && sm.Mp3BlobPath != null)
            .Select(sm => new { sm.Id, sm.NumberOfStreams })
            .ToListAsync();

        var results = popularByLikes
            .Where(p => validSongs.Any(v => v.Id == p.SongId))
            .Select(p => 
            {
                var song = validSongs.First(v => v.Id == p.SongId);
                // Combine like count and stream count for scoring
                var score = (double)p.LikeCount * 2 + song.NumberOfStreams;
                return (p.SongId, score);
            })
            .OrderByDescending(x => x.score)
            .Take(limit)
            .ToList();

        // If still not enough, get any playable songs
        if (results.Count < limit)
        {
            var existingSongIds = results.Select(r => r.SongId).Concat(excludeSongIds).ToHashSet();
            
            var additionalSongs = await context.SongMetadata
                .Where(sm => !sm.IsAlbumCover && sm.Mp3BlobPath != null)
                .Where(sm => !existingSongIds.Contains(sm.Id))
                .OrderByDescending(sm => sm.NumberOfStreams)
                .Take(limit - results.Count)
                .Select(sm => new { sm.Id, sm.NumberOfStreams })
                .ToListAsync();

            results.AddRange(additionalSongs.Select(s => (s.Id, (double)s.NumberOfStreams)));
        }

        return results;
    }

    /// <summary>
    /// Model for Supabase recommendation RPC result
    /// </summary>
    private class SupabaseRecommendation
    {
        public int SongId { get; set; }
        public double Score { get; set; }
    }

    /// <summary>
    /// Model for syncing song likes to Supabase
    /// </summary>
    private class SupabaseSongLike : Supabase.Postgrest.Models.BaseModel
    {
        [Supabase.Postgrest.Attributes.PrimaryKey("user_id", false)]
        [Supabase.Postgrest.Attributes.Column("user_id")]
        public int UserId { get; set; }

        [Supabase.Postgrest.Attributes.PrimaryKey("song_metadata_id", false)]
        [Supabase.Postgrest.Attributes.Column("song_metadata_id")]
        public int SongMetadataId { get; set; }

        [Supabase.Postgrest.Attributes.Column("is_like")]
        public bool IsLike { get; set; }

        [Supabase.Postgrest.Attributes.Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Supabase.Postgrest.Attributes.Column("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}
