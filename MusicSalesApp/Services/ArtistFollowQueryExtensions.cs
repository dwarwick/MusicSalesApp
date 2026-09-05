using System.Linq.Expressions;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// The shared filters for the follow feature.
/// </summary>
/// <remarks>
/// "Is this artist available?" is asked by the follow service, the message service and the release
/// notification job, and the answer has four parts that are easy to write two of. Defining it once
/// here is what makes "an artist who is suspended stops messaging and stops notifying" a single
/// fact rather than three copies that can drift.
/// </remarks>
internal static class ArtistFollowQueryExtensions
{
    /// <summary>
    /// A persona listeners may follow and that may reach them: enabled, owned by an active
    /// creator, whose account is not suspended.
    /// </summary>
    private static readonly Expression<Func<CreatorPersona, bool>> PubliclyActivePersonaFilter =
        persona => persona.IsEnabled &&
                   persona.Name != null &&
                   persona.Name != string.Empty &&
                   persona.Creator != null &&
                   persona.Creator.IsActive &&
                   (persona.Creator.User == null || !persona.Creator.User.IsSuspended);

    public static IQueryable<CreatorPersona> WherePubliclyActive(this IQueryable<CreatorPersona> query)
    {
        return query.Where(PubliclyActivePersonaFilter);
    }

    /// <summary>
    /// A live follow relationship. Blocked rows are excluded here rather than at each call site,
    /// because every question the feature asks of a follow - notify them? may the artist message
    /// them? do they count as a follower? - answers no once the listener has blocked the artist.
    /// </summary>
    private static readonly Expression<Func<ArtistFollower, bool>> ActiveFollowFilter =
        follow => follow.IsActive && !follow.IsBlockedByListener;

    public static IQueryable<ArtistFollower> WhereActiveFollow(this IQueryable<ArtistFollower> query)
    {
        return query.Where(ActiveFollowFilter);
    }

    /// <summary>
    /// A song whose becoming public is worth telling followers about: playable, live, not an album
    /// cover, and attributed to a persona somebody could have followed.
    /// </summary>
    /// <remarks>
    /// The PersonaId requirement is not incidental. A song whose artist is only free text has no
    /// artist entity, so nobody can be following it - filtering here rather than discovering the
    /// empty follower list later keeps the job's intent legible.
    /// </remarks>
    private static readonly Expression<Func<SongMetadata, bool>> PubliclyReleasedSongFilter =
        song => song.IsActive &&
                song.IsEnabled &&
                !song.IsAlbumCover &&
                song.Mp3BlobPath != null &&
                song.Mp3BlobPath != string.Empty &&
                song.PersonaId != null;

    public static IQueryable<SongMetadata> WherePubliclyReleased(this IQueryable<SongMetadata> query)
    {
        return query.Where(PubliclyReleasedSongFilter);
    }
}
