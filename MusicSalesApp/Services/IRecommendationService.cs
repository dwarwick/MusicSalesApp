using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating song recommendations based on user likes and collaborative filtering
/// </summary>
public interface IRecommendationService
{
    /// <summary>
    /// Get recommended songs for a user. Generates recommendations if they don't exist or are older than 24 hours.
    /// In DEBUG mode, always generates fresh recommendations.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <returns>List of recommended playlist items (up to 20 songs)</returns>
    Task<List<RecommendedPlaylist>> GetRecommendedPlaylistAsync(int userId);

    /// <summary>
    /// Force regeneration of recommendations for a user
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <returns>List of newly generated recommended playlist items</returns>
    Task<List<RecommendedPlaylist>> GenerateRecommendationsAsync(int userId);

    /// <summary>
    /// Check if a user has fresh recommendations (generated within the last 24 hours)
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <returns>True if fresh recommendations exist</returns>
    Task<bool> HasFreshRecommendationsAsync(int userId);

    /// <summary>
    /// Sync likes data to Supabase for collaborative filtering
    /// </summary>
    Task SyncLikesToSupabaseAsync();
}
