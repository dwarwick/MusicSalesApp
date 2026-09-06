#nullable enable
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// The two account-level email switches for the follow feature.
/// </summary>
/// <remarks>
/// A service rather than the pages reading <c>ApplicationUser</c> directly, because there are three
/// callers - Manage Account, the mobile API, and the tests - and the suspension rule below has to
/// hold for all of them.
/// </remarks>
public interface IArtistNotificationPreferenceService
{
    /// <summary>
    /// The user's current preferences, or null if there is no such user.
    /// </summary>
    Task<ArtistNotificationPreferences?> GetAsync(int userId, CancellationToken cancellationToken = default);

    Task<bool> SetAsync(
        int userId,
        ArtistNotificationPreferences preferences,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The account-level channel switches: email and push, per notification kind.
/// </summary>
/// <remarks>
/// <para>
/// There are deliberately no in-app switches here. The notification row IS the in-app notification,
/// and the per-artist mute on <c>ArtistFollower</c> already silences it - a third switch that
/// suppressed the row itself would mean a listener could mute an artist so thoroughly they could
/// never find out they had.
/// </para>
/// <para>
/// Email and push are separate per kind rather than one switch each, because they are genuinely
/// different experiences: wanting a phone alert the moment an artist releases something, while not
/// wanting mail about it, is an ordinary preference and not a contradiction.
/// </para>
/// </remarks>
public sealed class ArtistNotificationPreferences
{
    public bool ReceiveArtistReleaseEmails { get; set; }

    public bool ReceiveArtistMessageEmails { get; set; }

    public bool ReceiveArtistReleasePush { get; set; }

    public bool ReceiveArtistMessagePush { get; set; }

    /// <summary>
    /// How often push may interrupt. Governs BOTH kinds, unlike the switches above.
    /// </summary>
    /// <remarks>
    /// A frequency is not a fifth on/off switch: the two push booleans still decide whether a kind
    /// is sent at all, and this decides how often whatever survives them is allowed to buzz. The
    /// pair is worth keeping separate, because "I want release alerts, just not six of them at
    /// once" is an ordinary thing to want and the booleans alone cannot express it.
    /// </remarks>
    public ArtistPushFrequency ArtistPushFrequency { get; set; }
}
