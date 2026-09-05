#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// The creator's follower headline figures.
/// </summary>
/// <remarks>
/// Kept apart from <c>IDashboardService</c>, which answers stream questions over an arbitrary
/// window with genre/artist/song cross-filtering. These are three fixed numbers about a single
/// persona, and folding them into that service would mean widening its filter model to carry a
/// dimension none of its other callers have.
/// </remarks>
public interface IArtistFollowerAnalyticsService
{
    /// <summary>
    /// Totals for one persona, or null when the persona is not <paramref name="creatorId"/>'s.
    /// </summary>
    Task<ArtistFollowerAnalyticsDto?> GetAnalyticsAsync(
        int creatorPersonaId,
        int creatorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Active followers across every persona this creator owns, for the dashboard tile.
    /// </summary>
    Task<int> GetTotalFollowersForCreatorAsync(int creatorId, CancellationToken cancellationToken = default);
}
