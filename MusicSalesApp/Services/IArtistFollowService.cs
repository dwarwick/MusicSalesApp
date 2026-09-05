#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Creating, ending and reading follow relationships, from the listener's side.
/// </summary>
public interface IArtistFollowService
{
    /// <summary>
    /// Brings the follow to the requested state. Idempotent: the outcome depends only on
    /// <paramref name="following"/>, never on what it was before.
    /// </summary>
    /// <remarks>
    /// This is the entry point the mobile client uses. Its offline queue replays intents in
    /// order after a reconnect, so an operation whose meaning depends on the current state - a
    /// toggle - would flip the wrong way whenever a replay landed twice. Same reasoning as
    /// <c>PUT api/music/like-state/{id}</c>.
    /// </remarks>
    Task<ArtistFollowOutcome> SetFollowStateAsync(
        int creatorPersonaId,
        int listenerUserId,
        bool following,
        int? sourceSongMetadataId = null,
        int? followAsPersonaId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this listener has consented to being named to artists they follow, and which of
    /// their own personas they could follow as.
    /// </summary>
    /// <remarks>
    /// Called on click rather than on render - a library page has hundreds of Follow bells and this
    /// is a per-user question, so asking once when someone actually acts is the whole difference.
    /// </remarks>
    Task<FollowAsOptionsDto> GetFollowAsOptionsAsync(
        int listenerUserId,
        CancellationToken cancellationToken = default);

    Task<bool> IsFollowingAsync(int creatorPersonaId, int listenerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of these personas the listener follows. Bulk because a card grid or a track list asks
    /// about every artist on screen at once.
    /// </summary>
    Task<IReadOnlySet<int>> GetFollowedPersonaIdsAsync(
        IEnumerable<int> creatorPersonaIds,
        int listenerUserId,
        CancellationToken cancellationToken = default);

    Task<int> GetFollowerCountAsync(int creatorPersonaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Follower counts for several personas in one query, mirroring
    /// <c>ICreatorPersonaService.GetPersonaSongCountsAsync</c>. Personas with no followers are
    /// present with a count of zero, so a caller never has to distinguish missing from empty.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> GetFollowerCountsAsync(
        IEnumerable<int> creatorPersonaIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The listener's "Artists You Follow" list, newest follow first.
    /// </summary>
    Task<IReadOnlyList<FollowedArtistDto>> GetFollowedArtistsAsync(
        int listenerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mutes or unmutes one artist for this listener without ending the follow. Null leaves a
    /// preference untouched.
    /// </summary>
    Task<bool> SetArtistNotificationPreferencesAsync(
        int creatorPersonaId,
        int listenerUserId,
        bool? releaseNotificationsEnabled,
        bool? artistMessagesEnabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks or unblocks an artist. Blocking also ends the follow; unblocking does not restore
    /// it, because a listener who blocked someone has not asked to hear from them again.
    /// </summary>
    Task<bool> SetBlockedAsync(
        int creatorPersonaId,
        int listenerUserId,
        bool blocked,
        CancellationToken cancellationToken = default);
}

// There is deliberately no DeactivateFollowsForPersona/ForCreator here, and the omission is worth
// recording because it looks like a gap:
//
//   - A persona being DELETED already takes its follows with it, by cascade. StopBeingCreatorAsync
//     and account deletion both go through DeleteAllPersonasForCreatorAsync, so both are covered.
//   - A persona being DISABLED, or its creator going inactive or being suspended, is handled by
//     ArtistFollowQueryExtensions.WherePubliclyActive - the artist goes silent everywhere at once
//     without touching a single row.
//
// A method that deactivated follows in bulk would therefore have no correct caller, and one
// incorrect one that is easy to reach for: disabling a persona is reversible, so tearing down the
// follower base on a disable would destroy something re-enabling cannot restore.
