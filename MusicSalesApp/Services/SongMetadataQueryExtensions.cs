using System.Linq.Expressions;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

internal static class SongMetadataQueryExtensions
{
    private static readonly Expression<Func<SongMetadata, bool>> ActivePlayableSongsFilter =
        song => song.IsActive &&
                song.Mp3BlobPath != null &&
                song.Mp3BlobPath != string.Empty;

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

    public static IQueryable<SongMetadata> WhereActivePlayableSongs(this IQueryable<SongMetadata> query)
    {
        return query.Where(ActivePlayableSongsFilter);
    }

    public static IQueryable<SongMetadata> WhereActiveSongsFromActiveCreatorsIncludingDisabled(this IQueryable<SongMetadata> query)
    {
        return query.Where(ActiveSongsFromActiveCreatorsIncludingDisabledFilter);
    }

    public static IQueryable<SongMetadata> WhereVisibleLibrarySongs(this IQueryable<SongMetadata> query)
    {
        return query.Where(VisibleLibrarySongsFilter);
    }

    private static readonly Expression<Func<SongMetadata, bool>> CompleteProfileFilter =
        song => song.ImageBlobPath != null &&
                song.ImageBlobPath != string.Empty &&
                song.Genre != null &&
                song.Genre != string.Empty &&
                song.PersonaId != null &&
                song.Persona!.IsEnabled &&
                song.Persona.Name != null &&
                song.Persona.Name != string.Empty &&
                song.Persona.ImageBlobPath != null &&
                song.Persona.ImageBlobPath != string.Empty;

    public static IQueryable<SongMetadata> WhereHasCompleteProfile(this IQueryable<SongMetadata> query)
    {
        return query.Where(CompleteProfileFilter);
    }
}