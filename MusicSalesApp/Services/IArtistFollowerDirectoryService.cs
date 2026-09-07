#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// The creator's view of who follows them.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place a creator can see anything at all about their followers, and it is
/// separate from <see cref="IArtistFollowService"/> for that reason: everything it returns is an
/// <see cref="ArtistFollowerSummaryDto"/>, which has no field capable of holding an email, a
/// username or a listener id. The privacy promise is therefore a property of the type rather than
/// a rule someone has to remember while writing a query.
/// </para>
/// <para>
/// Every method takes the calling creator's id and checks it against the persona's owner. A
/// creator asking about someone else's persona gets null, not an empty list - "not yours" and
/// "nobody follows you" are different answers and the caller must not confuse them.
/// </para>
/// </remarks>
public interface IArtistFollowerDirectoryService
{
    /// <summary>
    /// The persona's active followers, newest first, or null when the persona is not
    /// <paramref name="creatorId"/>'s.
    /// </summary>
    Task<IReadOnlyList<ArtistFollowerSummaryDto>?> GetFollowersAsync(
        int creatorPersonaId,
        int creatorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the persona belongs to this creator. Exposed so a page can tell a bad id from an
    /// empty follower list before rendering.
    /// </summary>
    Task<bool> OwnsPersonaAsync(int creatorPersonaId, int creatorId, CancellationToken cancellationToken = default);
}
