namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Android notification channel ids.
/// </summary>
/// <remarks>
/// <b>The server writes this into every FCM payload and the Android app creates the channel with
/// it.</b> They must be identical: from Android 8 a notification whose channel does not exist is
/// dropped by the system with no error, no log line and nothing on screen - which looks exactly
/// like push not working at all. Both repos reference this project, so the two ends cannot drift.
/// </remarks>
public static class PushNotificationChannels
{
    /// <summary>
    /// New releases and messages from artists a listener follows. One channel rather than two, so
    /// a listener who mutes it in Android settings mutes the feature rather than half of it -
    /// per-artist and per-kind control already exists inside the app, where it can be explained.
    /// </summary>
    /// <remarks>
    /// The value is prefixed because a channel id is scoped to the app, not to a feature, and sits
    /// alongside the playback channel - an unprefixed "artist_updates" reads as ambiguous next to
    /// it. Changing this string orphans the channel on every device that already created one, so
    /// treat it as fixed.
    /// </remarks>
    public const string ArtistUpdates = "streamtunes_artist_updates";

    /// <summary>Human-readable channel name, shown in Android's notification settings.</summary>
    public const string ArtistUpdatesName = "Artist updates";

    public const string ArtistUpdatesDescription =
        "New music and messages from artists you follow on StreamTunes.";
}

/// <summary>
/// Keys in the push payload's data block, read by the client to decide where a tap lands.
/// </summary>
/// <remarks>
/// Written by the server's dispatcher, read by the mobile client - the shared-constant rule in
/// AGENTS.md applies exactly. A mismatch here does not error; the tap just opens the home screen.
/// </remarks>
public static class PushDataKeys
{
    /// <summary>One of <see cref="PushNotificationKinds"/>.</summary>
    public const string Kind = "kind";

    /// <summary>The persona the notification is about.</summary>
    public const string PersonaId = "personaId";

    /// <summary>The song, for a release notification.</summary>
    public const string SongId = "songId";

    /// <summary>The in-app notification or message row, so the client can mark it read on open.</summary>
    public const string EntityId = "entityId";
}

/// <summary>
/// What a push is about. Values are also the <see cref="PushDataKeys.Kind"/> payload values.
/// </summary>
public static class PushNotificationKinds
{
    public const string Release = "release";

    public const string ArtistMessage = "artist-message";
}
