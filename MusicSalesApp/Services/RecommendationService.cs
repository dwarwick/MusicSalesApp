using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using Supabase;
using System.Text.Json.Serialization;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating song recommendations using collaborative filtering via Supabase + pgvector.
/// 
/// <para>
/// <b>Supabase Setup Requirements:</b>
/// When Supabase is configured, this service expects the following setup in your Supabase database:
/// </para>
/// 
/// <para>
/// <b>1. song_likes table:</b>
/// <code>
/// CREATE TABLE song_likes (
///     user_id INTEGER NOT NULL,
///     song_metadata_id INTEGER NOT NULL,
///     is_like BOOLEAN NOT NULL,
///     created_at TIMESTAMP NOT NULL DEFAULT NOW(),
///     updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
///     embedding vector(384),
///     PRIMARY KEY (user_id, song_metadata_id)
/// );
/// </code>
/// </para>
/// 
/// <para>
/// <b>2. get_recommendations RPC function:</b>
/// <code>
/// CREATE OR REPLACE FUNCTION get_recommendations(p_user_id INTEGER, p_limit INTEGER, p_exclude_songs INTEGER[])
/// RETURNS TABLE(song_id INTEGER, score DOUBLE PRECISION) AS $$
/// BEGIN
///     -- Implement your recommendation logic using pgvector here
///     -- Return song_id and recommendation score
/// END;
/// $$ LANGUAGE plpgsql;
/// </code>
/// </para>
/// 
/// <para>
/// If Supabase is not configured, the service falls back to local collaborative filtering 
/// using the SQL Server database.
/// </para>
/// </summary>
public class RecommendationService : IRecommendationService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<RecommendationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IOpenAIEmbeddingService _embeddingService;
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private const int MaxRecommendations = 20;
    private const string SupabaseRpcFunctionName = "get_recommendations";

    public RecommendationService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<RecommendationService> logger,
        IConfiguration configuration,
        IOpenAIEmbeddingService embeddingService)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _configuration = configuration;
        _embeddingService = embeddingService;
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

            // Validate that recommended song IDs exist in SQL Server SongMetadata table
            // (Supabase may return IDs that don't exist locally)
            var recommendedSongIds = recommendedSongs.Select(r => r.SongId).ToList();
            var validSongIds = await context.SongMetadata
                .Where(sm => recommendedSongIds.Contains(sm.Id) && !sm.IsAlbumCover)
                .Select(sm => sm.Id)
                .ToListAsync();
            
            var validSongIdSet = new HashSet<int>(validSongIds);

            // Create RecommendedPlaylist entries (only for valid song IDs)
            var recommendations = new List<RecommendedPlaylist>();
            var displayOrder = 1;

            foreach (var (songId, score) in recommendedSongs.Where(r => validSongIdSet.Contains(r.SongId)).Take(MaxRecommendations))
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

            // Get all likes from SQL Server with song metadata for embedding generation
            var allLikes = await context.SongLikes
                .Include(sl => sl.SongMetadata)
                .Select(sl => new
                {
                    sl.UserId,
                    sl.SongMetadataId,
                    sl.IsLike,
                    sl.CreatedAt,
                    sl.UpdatedAt,
                    SongName = sl.SongMetadata != null ? sl.SongMetadata.Mp3BlobPath : null,
                    AlbumName = sl.SongMetadata != null ? sl.SongMetadata.AlbumName : null,
                    Genre = sl.SongMetadata != null ? sl.SongMetadata.Genre : null
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

            // Generate embeddings for songs if OpenAI is configured
            var songEmbeddings = new Dictionary<int, float[]>();
            if (_embeddingService.IsConfigured)
            {
                // Get unique songs that need embeddings
                var uniqueSongs = allLikes
                    .GroupBy(l => l.SongMetadataId)
                    .Select(g => g.First())
                    .ToList();

                foreach (var song in uniqueSongs)
                {
                    try
                    {
                        // Create embedding text from song metadata
                        var songFileName = !string.IsNullOrEmpty(song.SongName) 
                            ? Path.GetFileNameWithoutExtension(song.SongName) 
                            : $"Song {song.SongMetadataId}";
                        var embeddingText = $"{songFileName} {song.AlbumName ?? ""} {song.Genre ?? ""}".Trim();
                        
                        var embedding = await _embeddingService.GenerateEmbeddingAsync(embeddingText);
                        if (embedding != null)
                        {
                            songEmbeddings[song.SongMetadataId] = embedding;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate embedding for song {SongId}", song.SongMetadataId);
                    }
                }

                _logger.LogInformation("Generated embeddings for {Count} unique songs", songEmbeddings.Count);
            }
            else
            {
                _logger.LogWarning("OpenAI is not configured. Syncing likes without embeddings.");
            }

            // Sync likes to Supabase song_likes table
            // See class documentation for required Supabase table schema
            foreach (var like in allLikes)
            {
                try
                {
                    var songLike = new SupabaseSongLike
                    {
                        UserId = like.UserId,
                        SongMetadataId = like.SongMetadataId,
                        IsLike = like.IsLike,
                        CreatedAt = like.CreatedAt,
                        UpdatedAt = like.UpdatedAt
                    };

                    // Add embedding if available
                    if (songEmbeddings.TryGetValue(like.SongMetadataId, out var embedding))
                    {
                        songLike.Embedding = $"[{string.Join(",", embedding)}]";
                    }

                    await supabase.From<SupabaseSongLike>()
                        .Upsert(songLike);
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

            // Call the Supabase RPC function for collaborative filtering recommendations
            // See class documentation for required Supabase setup
            var result = await supabase.Rpc(SupabaseRpcFunctionName, new Dictionary<string, object>
            {
                { "p_user_id", userId },
                { "p_limit", MaxRecommendations },
                { "p_exclude_songs", userDislikes }
            });

            if (result.Content == null)
            {
                return new List<(int SongId, double Score)>();
            }

            // Parse result - the RPC returns a list of {song_id, score}
            var recommendations = System.Text.Json.JsonSerializer.Deserialize<List<SupabaseRecommendation>>(
                result.Content, 
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // Filter out any recommendations with null scores
            return recommendations?
                .Where(r => r.Score.HasValue)
                .Select(r => (r.SongId, r.Score!.Value))
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
        [JsonPropertyName("song_id")]
        public int SongId { get; set; }
        public double? Score { get; set; }
    }

    /// <summary>
    /// Model for syncing song likes to Supabase.
    /// Maps to the song_likes table with composite primary key (user_id, song_metadata_id).
    /// See class documentation for the required Supabase table schema.
    /// </summary>
    [Supabase.Postgrest.Attributes.Table("song_likes")]
    private class SupabaseSongLike : Supabase.Postgrest.Models.BaseModel
    {
        // Composite primary key: (user_id, song_metadata_id)
        // The PrimaryKey attribute marks the first column of the composite key
        [Supabase.Postgrest.Attributes.PrimaryKey("user_id")]
        [Supabase.Postgrest.Attributes.Column("user_id")]
        public int UserId { get; set; }

        // Second part of composite primary key
        [Supabase.Postgrest.Attributes.Column("song_metadata_id")]
        public int SongMetadataId { get; set; }

        [Supabase.Postgrest.Attributes.Column("is_like")]
        public bool IsLike { get; set; }

        [Supabase.Postgrest.Attributes.Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Supabase.Postgrest.Attributes.Column("updated_at")]
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Vector embedding for the song (384 dimensions from text-embedding-3-small)
        /// Stored as a string in format "[0.1,0.2,...]" for Supabase pgvector
        /// </summary>
        [Supabase.Postgrest.Attributes.Column("embedding")]
        public string Embedding { get; set; }
    }
}
