using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating song recommendations using collaborative filtering.
/// Recommendations are generated nightly via a Hangfire job and cached in the RecommendedPlaylists table.
/// </summary>
public class RecommendationService : IRecommendationService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ITopStreamedPlaylistService _topStreamedPlaylistService;
    private readonly ILogger<RecommendationService> _logger;
    private const int MaxRecommendations = 20;

    public RecommendationService(
        IDbContextFactory<AppDbContext> contextFactory,
        ITopStreamedPlaylistService topStreamedPlaylistService,
        ILogger<RecommendationService> logger)
    {
        _contextFactory = contextFactory;
        _topStreamedPlaylistService = topStreamedPlaylistService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<List<RecommendedPlaylist>> GetRecommendedPlaylistAsync(int userId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.RecommendedPlaylists
                .Include(rp => rp.SongMetadata)
                    .ThenInclude(sm => sm.Creator)
                        .ThenInclude(c => c.User)
                // Without this the mobile mapper sees a null Persona and silently omits the artist
                // image and its rendition from every song. Lazy-loading proxies are off.
                .Include(rp => rp.SongMetadata)
                    .ThenInclude(sm => sm.Persona)
                .Where(rp => rp.UserId == userId)
                .Where(rp => rp.SongMetadata != null && rp.SongMetadata.IsEnabled && rp.SongMetadata.IsActive)
                .OrderBy(rp => rp.DisplayOrder)
                .ToListAsync();
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
            return await GenerateRecommendationsCoreAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating recommendations for user {UserId}", userId);
            return new List<RecommendedPlaylist>();
        }
    }

    /// <inheritdoc/>
    public async Task GenerateAllRecommendationsAsync()
    {
        // The five global playlists first, and in their own try/catch. They are a handful of queries
        // whereas the per-user loop below is one pass per user with likes, so running them first
        // means the playlists are not held hostage by a slow loop - and a failure in either half must
        // not cost the other.
        try
        {
            await _topStreamedPlaylistService.GenerateAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating top-streamed playlists");
        }

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Find all users who have at least one like (exclude dislike-only users)
            var userIds = await context.SongLikes
                .Where(sl => sl.IsLike)
                .Select(sl => sl.UserId)
                .Distinct()
                .ToListAsync();

            _logger.LogInformation("Generating recommendations for {Count} users", userIds.Count);

            var successCount = 0;
            var failureCount = 0;
            foreach (var userId in userIds)
            {
                try
                {
                    await GenerateRecommendationsCoreAsync(userId);
                    successCount++;
                }
                catch (Exception ex)
                {
                    failureCount++;
                    _logger.LogWarning(ex, "Failed to generate recommendations for user {UserId}", userId);
                }
            }

            _logger.LogInformation("Successfully generated recommendations for {SuccessCount}/{TotalCount} users, {FailureCount} failed",
                successCount, userIds.Count, failureCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating recommendations for all users");
            throw;
        }
    }

    /// <summary>
    /// Core recommendation generation logic that propagates exceptions.
    /// Called by <see cref="GenerateRecommendationsAsync"/> (which swallows errors) and
    /// <see cref="GenerateAllRecommendationsAsync"/> (which catches per-user failures directly).
    /// </summary>
    private async Task<List<RecommendedPlaylist>> GenerateRecommendationsCoreAsync(int userId)
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

        var recommendedSongs = await GetLocalRecommendationsAsync(context, userId, userLikes, userDislikes);

        // Validate recommended song IDs and filter out disabled/inactive songs
        var recommendedSongIds = recommendedSongs.Select(r => r.SongId).ToList();
        var validSongIds = await context.SongMetadata
            .Where(sm => recommendedSongIds.Contains(sm.Id) && !sm.IsAlbumCover && sm.IsEnabled && sm.IsActive)
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
        }

        // Always persist: ensures removals are saved even when no new recommendations are generated
        await context.SaveChangesAsync();

        if (recommendations.Any())
        {
            // Reload with navigation properties
            return await context.RecommendedPlaylists
                .Include(rp => rp.SongMetadata)
                    .ThenInclude(sm => sm.Creator)
                        .ThenInclude(c => c.User)
                .Include(rp => rp.SongMetadata)
                    .ThenInclude(sm => sm.Persona)
                .Where(rp => rp.UserId == userId)
                .OrderBy(rp => rp.DisplayOrder)
                .ToListAsync();
        }

        return recommendations;
    }

    /// <summary>
    /// Get recommendations using local collaborative filtering algorithm.
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

        // Verify these songs exist and are playable (have Mp3BlobPath), enabled, and active
        var validSongIds = await context.SongMetadata
            .Where(sm => songScores.Select(s => s.SongId).Contains(sm.Id))
            .Where(sm => !sm.IsAlbumCover && sm.Mp3BlobPath != null && sm.IsEnabled && sm.IsActive)
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

        // Verify these songs exist and are playable, enabled, and active
        var validSongs = await context.SongMetadata
            .Where(sm => popularByLikes.Select(p => p.SongId).Contains(sm.Id))
            .Where(sm => !sm.IsAlbumCover && sm.Mp3BlobPath != null && sm.IsEnabled && sm.IsActive)
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
                .Where(sm => !sm.IsAlbumCover && sm.Mp3BlobPath != null && sm.IsEnabled && sm.IsActive)
                .Where(sm => !existingSongIds.Contains(sm.Id))
                .OrderByDescending(sm => sm.NumberOfStreams)
                .Take(limit - results.Count)
                .Select(sm => new { sm.Id, sm.NumberOfStreams })
                .ToListAsync();

            results.AddRange(additionalSongs.Select(s => (s.Id, (double)s.NumberOfStreams)));
        }

        return results;
    }
}
