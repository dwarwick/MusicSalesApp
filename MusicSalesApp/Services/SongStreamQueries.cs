using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

/// <summary>
/// The single definition of "this user has streamed this song", shared by everything that needs it.
///
/// Two features depend on the same question and must never drift apart: the featured-song free-stream
/// cap in <see cref="StreamCountService"/>, and the rule that a user may only rate a song they have
/// actually listened to (<see cref="SongLikeService"/>). Both are answered from <c>SongStreams</c> rows
/// whose <c>StreamerUserId</c> matches the caller - a row left by an anonymous listener has a null
/// <c>StreamerUserId</c> and deliberately confers nothing on the account that later signs in.
///
/// These take an <see cref="AppDbContext"/> rather than creating one so callers can reuse the context
/// they already hold; <see cref="IStreamCountService"/> exposes context-creating wrappers for callers
/// that have none.
/// </summary>
internal static class SongStreamQueries
{
    /// <summary>
    /// True when <paramref name="userId"/> has at least one recorded stream of <paramref name="songMetadataId"/>.
    /// Uses the IX_SongStreams_SongMetadataId / IX_SongStreams_StreamerUserId indexes.
    /// </summary>
    public static Task<bool> HasUserStreamedAsync(AppDbContext context, int songMetadataId, int userId)
    {
        return context.SongStreams.AnyAsync(stream =>
            stream.SongMetadataId == songMetadataId &&
            stream.StreamerUserId == userId);
    }

    /// <summary>
    /// The subset of <paramref name="songMetadataIds"/> that <paramref name="userId"/> has streamed.
    /// Bulk form of <see cref="HasUserStreamedAsync"/> for the per-song-list clients.
    /// </summary>
    public static async Task<HashSet<int>> GetUserStreamedSongIdsAsync(
        AppDbContext context,
        int userId,
        IEnumerable<int> songMetadataIds)
    {
        var ids = songMetadataIds as IList<int> ?? songMetadataIds.ToList();
        if (ids.Count == 0)
            return new HashSet<int>();

        var streamedIds = await context.SongStreams
            .Where(stream => stream.StreamerUserId == userId && ids.Contains(stream.SongMetadataId))
            .Select(stream => stream.SongMetadataId)
            .Distinct()
            .ToListAsync();

        return streamedIds.ToHashSet();
    }
}
