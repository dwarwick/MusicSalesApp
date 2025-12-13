using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for cleaning up playlist songs for users with lapsed subscriptions
/// </summary>
public class PlaylistCleanupService : IPlaylistCleanupService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<PlaylistCleanupService> _logger;

    public PlaylistCleanupService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<PlaylistCleanupService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Removes songs from playlists for users whose subscriptions have lapsed
    /// and they don't own the songs outright.
    /// Uses a 48-hour grace period to account for potential job execution delays.
    /// </summary>
    /// <returns>Number of songs removed</returns>
    public async Task<int> RemoveNonOwnedSongsFromLapsedSubscriptionsAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            var now = DateTime.UtcNow;
            var gracePeriodCutoff = now.AddHours(-48); // 48 hour grace period
            
            // Find users with lapsed subscriptions (CANCELLED or EXPIRED status, EndDate passed with 48h grace)
            var lapsedUserIds = await context.Subscriptions
                .Where(s => (s.Status == "CANCELLED" || s.Status == "EXPIRED") &&
                           s.EndDate.HasValue &&
                           s.EndDate.Value < gracePeriodCutoff)
                .Select(s => s.UserId)
                .Distinct()
                .ToListAsync();

            if (!lapsedUserIds.Any())
            {
                _logger.LogInformation("No users with lapsed subscriptions found");
                return 0;
            }

            _logger.LogInformation("Found {Count} users with lapsed subscriptions", lapsedUserIds.Count);

            int totalRemoved = 0;

            foreach (var userId in lapsedUserIds)
            {
                // Check if user currently has an active subscription
                var hasActiveSubscription = await context.Subscriptions
                    .AnyAsync(s => s.UserId == userId &&
                                  s.Status == "ACTIVE" &&
                                  (!s.EndDate.HasValue || s.EndDate.Value > now));

                if (hasActiveSubscription)
                {
                    _logger.LogInformation("User {UserId} has an active subscription, skipping cleanup", userId);
                    continue;
                }

                // Get all songs in user's playlists
                var userPlaylistSongs = await context.UserPlaylists
                    .Include(up => up.OwnedSong)
                    .Where(up => up.UserId == userId)
                    .ToListAsync();

                // Find songs that are NOT owned (no PayPalOrderId means they were only accessible via subscription)
                var nonOwnedSongs = userPlaylistSongs
                    .Where(up => string.IsNullOrEmpty(up.OwnedSong.PayPalOrderId))
                    .ToList();

                if (nonOwnedSongs.Any())
                {
                    _logger.LogInformation(
                        "Removing {Count} non-owned songs from playlists for user {UserId}",
                        nonOwnedSongs.Count,
                        userId);

                    context.UserPlaylists.RemoveRange(nonOwnedSongs);
                    totalRemoved += nonOwnedSongs.Count;
                }
            }

            if (totalRemoved > 0)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation("Successfully removed {Count} non-owned songs from playlists", totalRemoved);
            }

            return totalRemoved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing non-owned songs from lapsed subscriptions");
            throw;
        }
    }
}
