using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating song recommendations based on user likes and collaborative filtering
/// </summary>
public interface IRecommendationService
{
    /// <summary>
    /// Get cached recommended songs for a user. Returns whatever is in the RecommendedPlaylists table.
    /// Recommendations are generated nightly by a Hangfire job.
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
    /// Generate recommendations for all users who have at least one song like.
    /// Called nightly by a Hangfire job.
    /// </summary>
    Task GenerateAllRecommendationsAsync();
}
