using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing song likes and dislikes
/// </summary>
public class SongLikeService : ISongLikeService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IHubContext<LikeCountHub> _hubContext;

    public SongLikeService(
        IDbContextFactory<AppDbContext> contextFactory,
        IHubContext<LikeCountHub> hubContext)
    {
        _contextFactory = contextFactory;
        _hubContext = hubContext;
    }

    /// <inheritdoc/>
    public async Task<(int likeCount, int dislikeCount)> GetLikeCountsAsync(int songMetadataId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var likes = await context.SongLikes
            .Where(sl => sl.SongMetadataId == songMetadataId)
            .ToListAsync();

        var likeCount = likes.Count(sl => sl.IsLike);
        var dislikeCount = likes.Count(sl => !sl.IsLike);

        return (likeCount, dislikeCount);
    }

    /// <inheritdoc/>
    public async Task<bool?> GetUserLikeStatusAsync(int userId, int songMetadataId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var songLike = await context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);

        return songLike?.IsLike;
    }

    /// <inheritdoc/>
    public async Task<bool> ToggleLikeAsync(int userId, int songMetadataId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var existingLike = await context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);

        if (existingLike != null)
        {
            if (existingLike.IsLike)
            {
                // User already liked, remove the like
                context.SongLikes.Remove(existingLike);
                await context.SaveChangesAsync();
                await BroadcastLikeCountsAsync(songMetadataId);
                return false;
            }
            else
            {
                // User disliked, change to like
                existingLike.IsLike = true;
                existingLike.UpdatedAt = DateTime.UtcNow;
                context.Entry(existingLike).State = EntityState.Modified;
                await context.SaveChangesAsync();
                await BroadcastLikeCountsAsync(songMetadataId);
                return true;
            }
        }
        else
        {
            // No existing like/dislike, create a new like
            var newLike = new SongLike
            {
                UserId = userId,
                SongMetadataId = songMetadataId,
                IsLike = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            context.SongLikes.Add(newLike);
            await context.SaveChangesAsync();
            await BroadcastLikeCountsAsync(songMetadataId);
            return true;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ToggleDislikeAsync(int userId, int songMetadataId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var existingLike = await context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);

        if (existingLike != null)
        {
            if (!existingLike.IsLike)
            {
                // User already disliked, remove the dislike
                context.SongLikes.Remove(existingLike);
                await context.SaveChangesAsync();
                await BroadcastLikeCountsAsync(songMetadataId);
                return false;
            }
            else
            {
                // User liked, change to dislike
                existingLike.IsLike = false;
                existingLike.UpdatedAt = DateTime.UtcNow;
                context.Entry(existingLike).State = EntityState.Modified;
                await context.SaveChangesAsync();
                await BroadcastLikeCountsAsync(songMetadataId);
                return true;
            }
        }
        else
        {
            // No existing like/dislike, create a new dislike
            var newDislike = new SongLike
            {
                UserId = userId,
                SongMetadataId = songMetadataId,
                IsLike = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            context.SongLikes.Add(newDislike);
            await context.SaveChangesAsync();
            await BroadcastLikeCountsAsync(songMetadataId);
            return true;
        }
    }

    /// <summary>
    /// Broadcasts updated like/dislike counts to all connected clients via SignalR.
    /// </summary>
    private async Task BroadcastLikeCountsAsync(int songMetadataId)
    {
        var (likeCount, dislikeCount) = await GetLikeCountsAsync(songMetadataId);
        await _hubContext.Clients.All.SendAsync(
            SignalRMethodNames.ReceiveLikeCountUpdate, songMetadataId, likeCount, dislikeCount);
    }

    /// <inheritdoc/>
    public async Task<List<int>> GetUserLikedSongIdsAsync(int userId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.SongLikes
            .Where(sl => sl.UserId == userId && sl.IsLike)
            .Select(sl => sl.SongMetadataId)
            .ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<Dictionary<int, int>> GetBulkLikeCountsAsync(IEnumerable<int> songMetadataIds)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var ids = songMetadataIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<int, int>();

        var likes = await context.SongLikes
            .Where(sl => ids.Contains(sl.SongMetadataId) && sl.IsLike)
            .Select(sl => sl.SongMetadataId)
            .ToListAsync();

        return likes
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <inheritdoc/>
    public async Task<Dictionary<int, (int likeCount, int dislikeCount)>> GetBulkLikeDislikeCountsAsync(IEnumerable<int> songMetadataIds)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var ids = songMetadataIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<int, (int likeCount, int dislikeCount)>();

        var counts = await context.SongLikes
            .Where(sl => ids.Contains(sl.SongMetadataId))
            .GroupBy(sl => sl.SongMetadataId)
            .Select(g => new
            {
                SongMetadataId = g.Key,
                LikeCount = g.Count(sl => sl.IsLike),
                DislikeCount = g.Count(sl => !sl.IsLike)
            })
            .ToListAsync();

        return counts.ToDictionary(
            c => c.SongMetadataId,
            c => (c.LikeCount, c.DislikeCount));
    }

    /// <inheritdoc/>
    public async Task<Dictionary<int, bool?>> GetBulkUserLikeStatusAsync(int userId, IEnumerable<int> songMetadataIds)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var ids = songMetadataIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<int, bool?>();

        var userLikes = await context.SongLikes
            .Where(sl => sl.UserId == userId && ids.Contains(sl.SongMetadataId))
            .Select(sl => new { sl.SongMetadataId, sl.IsLike })
            .ToListAsync();

        return userLikes.ToDictionary(
            ul => ul.SongMetadataId,
            ul => (bool?)ul.IsLike);
    }
}
