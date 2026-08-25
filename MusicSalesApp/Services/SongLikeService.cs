using System.Runtime.ExceptionServices;
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
    /// <summary>
    /// Configuration key for the stream-before-rating rule. See the "//Likes" note in appsettings.json:
    /// this gates only the server-side rejection, not the client-side gating in the web and mobile UIs,
    /// and exists so a server can go live ahead of a mobile release that is still in store review.
    /// Absent means enforce - a new environment is strict by default.
    /// </summary>
    internal const string RequireStreamBeforeRatingKey = "Likes:RequireStreamBeforeRating";

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IHubContext<LikeCountHub> _hubContext;
    private readonly bool _requireStreamBeforeRating;

    public SongLikeService(
        IDbContextFactory<AppDbContext> contextFactory,
        IHubContext<LikeCountHub> hubContext,
        IConfiguration configuration)
    {
        _contextFactory = contextFactory;
        _hubContext = hubContext;
        _requireStreamBeforeRating = configuration.GetValue(RequireStreamBeforeRatingKey, true);
    }

    /// <inheritdoc/>
    public async Task<(int likeCount, int dislikeCount)> GetLikeCountsAsync(int songMetadataId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Counted in the database rather than by materialising every row: this runs after every
        // like/dislike/set-state write and again inside the SignalR broadcast, so on a popular song the
        // old version pulled the whole like table for that song into memory several times per tap.
        // Same shape as GetBulkLikeDislikeCountsAsync, and it uses the SongMetadataId index.
        var counts = await context.SongLikes
            .Where(sl => sl.SongMetadataId == songMetadataId)
            .GroupBy(sl => sl.SongMetadataId)
            .Select(g => new
            {
                LikeCount = g.Count(sl => sl.IsLike),
                DislikeCount = g.Count(sl => !sl.IsLike)
            })
            .FirstOrDefaultAsync();

        // No rows at all for this song - GroupBy yields nothing rather than a zeroed group.
        return counts is null ? (0, 0) : (counts.LikeCount, counts.DislikeCount);
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

        // Flip semantics, unchanged: already liked becomes no opinion, anything else becomes liked.
        // The decision stays here - only the write is shared with the other two methods.
        var isLiked = existingLike is not { IsLike: true };

        await ApplyLikeStateAsync(context, existingLike, userId, songMetadataId, isLiked ? true : null);
        return isLiked;
    }

    /// <inheritdoc/>
    public async Task<bool> ToggleDislikeAsync(int userId, int songMetadataId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var existingLike = await context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);

        // Mirror of ToggleLikeAsync: already disliked becomes no opinion, anything else becomes disliked.
        var isDisliked = existingLike is not { IsLike: false };

        await ApplyLikeStateAsync(context, existingLike, userId, songMetadataId, isDisliked ? false : null);
        return isDisliked;
    }

    /// <inheritdoc/>
    public async Task<bool?> SetLikeStateAsync(int userId, int songMetadataId, bool? state)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var existingLike = await context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);

        // Already in the requested state - no write, and deliberately no broadcast, so replaying a
        // queued offline intent does not spam every connected client with an unchanged count.
        // Null-propagation makes this a lifted bool? comparison, so "no row and no opinion wanted"
        // is covered too.
        if (existingLike?.IsLike == state)
            return state;

        await ApplyLikeStateAsync(context, existingLike, userId, songMetadataId, state);
        return state;
    }

    /// <summary>
    /// Writes a terminal like state and broadcasts the new counts.
    ///
    /// Shared by the two toggles and <see cref="SetLikeStateAsync"/> so all three fail the same way:
    /// a concurrent writer is recovered from rather than surfaced, and a deleted song is reported as
    /// <see cref="SongNotFoundException"/> rather than an opaque write failure. The toggles keep their
    /// own flip decision and only hand the resulting state here - this is not one endpoint
    /// reimplemented in terms of another.
    /// </summary>
    /// <param name="existingLike">The row already read on <paramref name="context"/>, or null.</param>
    private async Task ApplyLikeStateAsync(
        AppDbContext context,
        SongLike existingLike,
        int userId,
        int songMetadataId,
        bool? state)
    {
        // Setting an opinion requires having streamed the song; clearing one never does. Both toggles and
        // SetLikeStateAsync land here, so this is the one place the rule has to be stated - including for
        // the Blazor app, which calls this service in-process and never passes through MusicController.
        //
        // Exempting the clear keeps a rating made before this rule (or one whose stream rows have since
        // been anonymised by an account deletion) retractable, rather than stranding the user with an
        // opinion they can see but not remove.
        if (_requireStreamBeforeRating &&
            state != null &&
            !await SongStreamQueries.HasUserStreamedAsync(context, songMetadataId, userId))
        {
            throw new LikeRequiresStreamException(songMetadataId, userId);
        }

        if (state == null)
        {
            context.SongLikes.Remove(existingLike!);
        }
        else if (existingLike != null)
        {
            existingLike.IsLike = state.Value;
            existingLike.UpdatedAt = DateTime.UtcNow;
            context.Entry(existingLike).State = EntityState.Modified;
        }
        else
        {
            context.SongLikes.Add(new SongLike
            {
                UserId = userId,
                SongMetadataId = songMetadataId,
                IsLike = state.Value,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException originalException)
        {
            // A deleted song can never be written, on this attempt or any other. Detect it before
            // retrying, both to skip a guaranteed-doomed second write and because the caller has to be
            // able to tell "this will never work" from "try again" - the mobile offline queue retries
            // the latter forever, and one poisoned intent blocks every intent queued behind it.
            //
            // Only when there is something to write. Clearing an opinion for a song that has since been
            // deleted has already achieved what the caller asked for - the cascade took the row with
            // it - and ForceLikeStateAsync treats that vanished row as success.
            if (state != null && !await SongExistsAsync(songMetadataId))
                throw new SongNotFoundException(songMetadataId, originalException);

            // Otherwise a concurrent request won the race against the unique (UserId, SongMetadataId)
            // index, or deleted the row we were about to modify. Re-read and apply the desired state on
            // top. If that fails too, surface the original failure rather than the retry's.
            try
            {
                await ForceLikeStateAsync(userId, songMetadataId, state);
            }
            catch (DbUpdateException)
            {
                ExceptionDispatchInfo.Capture(originalException).Throw();
            }
        }

        await BroadcastLikeCountsAsync(songMetadataId);
    }

    /// <summary>
    /// Fresh-context existence check, used only on the failure path of
    /// <see cref="ApplyLikeStateAsync"/> to tell a permanently unwritable song from a losable race.
    /// The context that just failed cannot be reused for this.
    /// </summary>
    private async Task<bool> SongExistsAsync(int songMetadataId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.SongMetadata.AnyAsync(song => song.Id == songMetadataId);
    }

    /// <summary>
    /// Second-attempt write for <see cref="ApplyLikeStateAsync"/> after a concurrent writer caused a
    /// <see cref="DbUpdateException"/>. Uses a fresh context so no stale tracked entities remain.
    /// </summary>
    private async Task ForceLikeStateAsync(int userId, int songMetadataId, bool? state)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var existingLike = await context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);

        if (state == null)
        {
            if (existingLike == null)
                return;

            context.SongLikes.Remove(existingLike);
        }
        else if (existingLike != null)
        {
            if (existingLike.IsLike == state.Value)
                return;

            existingLike.IsLike = state.Value;
            existingLike.UpdatedAt = DateTime.UtcNow;
            context.Entry(existingLike).State = EntityState.Modified;
        }
        else
        {
            context.SongLikes.Add(new SongLike
            {
                UserId = userId,
                SongMetadataId = songMetadataId,
                IsLike = state.Value,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync();
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
