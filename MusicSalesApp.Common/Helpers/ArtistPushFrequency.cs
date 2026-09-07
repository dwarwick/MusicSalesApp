namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// How often a listener is willing to be interrupted by artist push notifications.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is enforced on the server, before the send.</b> It cannot be a client-side filter: once
/// FCM has delivered a push the phone cannot retract it, and dropping it on arrival would lose the
/// notification rather than batch it - and iOS gives no guarantee of waking the app to make that
/// decision at all.
/// </para>
/// <para>
/// Anything other than <see cref="Instant"/> makes the dispatcher hold a listener's pending rows
/// until the oldest one has waited a full window, then send them as a single summary. So the
/// setting is a promise about the maximum number of interruptions, not about latency: at most one
/// push per window, however many things happened inside it.
/// </para>
/// <para>
/// Stored as an int on AspNetUsers, so the numbers are part of the database contract - append new
/// members, never renumber.
/// </para>
/// </remarks>
public enum ArtistPushFrequency
{
    /// <summary>Send as it happens. The default, and what everyone had before this existed.</summary>
    Instant = 0,

    TwelveHours = 1,

    Daily = 2,
}

/// <summary>
/// The windows behind <see cref="ArtistPushFrequency"/>, shared so the server's batching and the
/// app's wording cannot drift apart.
/// </summary>
public static class ArtistPushFrequencies
{
    public static TimeSpan WindowFor(ArtistPushFrequency frequency) => frequency switch
    {
        ArtistPushFrequency.TwelveHours => TimeSpan.FromHours(12),
        ArtistPushFrequency.Daily => TimeSpan.FromHours(24),
        _ => TimeSpan.Zero,
    };

    /// <summary>True when the dispatcher should hold rows rather than sending each one as it lands.</summary>
    public static bool IsBatched(ArtistPushFrequency frequency) => frequency != ArtistPushFrequency.Instant;

    /// <summary>The label used in both the web account page and the mobile config page.</summary>
    public static string DisplayName(ArtistPushFrequency frequency) => frequency switch
    {
        ArtistPushFrequency.TwelveHours => "At most every 12 hours",
        ArtistPushFrequency.Daily => "At most once a day",
        _ => "As it happens",
    };

    /// <summary>
    /// Parses the value stored on the user, tolerating anything unexpected.
    /// </summary>
    /// <remarks>
    /// A row written by a newer build, or a hand-edited column, must not stop a listener being
    /// notified at all - falling back to Instant fails towards delivering.
    /// </remarks>
    public static ArtistPushFrequency FromValue(int value) =>
        Enum.IsDefined(typeof(ArtistPushFrequency), value)
            ? (ArtistPushFrequency)value
            : ArtistPushFrequency.Instant;
}
