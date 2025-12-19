using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing song likes and dislikes
/// </summary>
public class SongLikeService : ISongLikeService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public SongLikeService(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
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
                return false;
            }
            else
            {
                // User disliked, change to like
                existingLike.IsLike = true;
                existingLike.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
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
                return false;
            }
            else
            {
                // User liked, change to dislike
                existingLike.IsLike = false;
                existingLike.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
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
            return true;
        }
    }
}
