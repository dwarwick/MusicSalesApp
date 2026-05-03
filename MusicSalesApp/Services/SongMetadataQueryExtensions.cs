using System.Linq.Expressions;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

internal static class SongMetadataQueryExtensions
{
    private static readonly Expression<Func<SongMetadata, bool>> ActiveSongsFromActiveCreatorsFilter =
        song => song.IsActive && song.IsEnabled && (song.CreatorId == null || song.Creator!.IsActive);

    private static readonly Expression<Func<SongMetadata, bool>> ActiveSongsFromActiveCreatorsIncludingDisabledFilter =
        song => song.IsActive && (song.CreatorId == null || song.Creator!.IsActive);

    private static readonly Expression<Func<SongMetadata, bool>> VisibleLibrarySongsFilter =
        song => song.IsActive &&
                song.IsEnabled &&
                song.Mp3BlobPath != null &&
                song.Mp3BlobPath != string.Empty &&
                (song.CreatorId == null || song.Creator!.IsActive);

    public static IQueryable<SongMetadata> WhereActiveSongsFromActiveCreators(this IQueryable<SongMetadata> query)
    {
        return query.Where(ActiveSongsFromActiveCreatorsFilter);
    }

    public static IQueryable<SongMetadata> WhereActiveSongsFromActiveCreatorsIncludingDisabled(this IQueryable<SongMetadata> query)
    {
        return query.Where(ActiveSongsFromActiveCreatorsIncludingDisabledFilter);
    }

    public static IQueryable<SongMetadata> WhereVisibleLibrarySongs(this IQueryable<SongMetadata> query)
    {
        return query.Where(VisibleLibrarySongsFilter);
    }
}