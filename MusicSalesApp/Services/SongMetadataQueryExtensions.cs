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

    /// <summary>
    /// Scores a materialized song's profile completeness as a percentage in 25% increments,
    /// for tiering purposes (e.g. display-order randomization). Cover art is a hard gate: a
    /// song with no cover art always scores 0%, regardless of what else is filled in — it is
    /// never awarded partial credit for genre/persona fields the way a song that merely lacks
    /// one of those does.
    ///
    /// A song scoring 100% here is, by construction, exactly the set of songs
    /// <see cref="WhereHasCompleteProfile"/> would select — the four checks below are the same
    /// four fields in <see cref="CompleteProfileFilter"/>. Keep the two in sync; the equivalence
    /// is asserted by SongMetadataQueryExtensionsTests.
    ///
    /// Requires the caller to have included <c>song.Persona</c> (no lazy-loading proxies are
    /// configured on <see cref="AppDbContext"/>); a null navigation with a non-null PersonaId
    /// (un-Included, or an orphaned FK) is treated the same as "no persona linked."
    /// </summary>
    public static int GetProfileCompletenessPercent(this SongMetadata song)
    {
        if (string.IsNullOrEmpty(song.ImageBlobPath))
        {
            return 0;
        }

        var completedFieldCount = 1; // cover art

        if (!string.IsNullOrEmpty(song.Genre))
        {
            completedFieldCount++;
        }

        var persona = song.Persona;
        var personaEligible = persona != null && persona.IsEnabled;

        if (personaEligible && !string.IsNullOrEmpty(persona!.Name))
        {
            completedFieldCount++;
        }

        if (personaEligible && !string.IsNullOrEmpty(persona!.ImageBlobPath))
        {
            completedFieldCount++;
        }

        return completedFieldCount * 25;
    }
}