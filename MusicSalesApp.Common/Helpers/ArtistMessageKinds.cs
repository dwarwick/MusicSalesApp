namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for ArtistFollowerMessage.MessageKind.
/// Always use these constants instead of inline strings to avoid mismatches between
/// writers (the message services) and readers (LINQ queries and the unique index filter).
/// </summary>
public static class ArtistMessageKinds
{
    /// <summary>
    /// The single artist-to-follower acknowledgement allowed in version 1. A filtered unique
    /// index on (ArtistFollowerId) WHERE MessageKind = this value is what enforces
    /// "one thank-you per follower, ever" in the schema rather than in a service.
    /// </summary>
    public const string ThankYou = "ThankYou";

    /// <summary>
    /// Reserved. Listener replies are deliberately NOT implemented in version 1 - the kind
    /// column exists so that adding them later is a new value rather than a dropped constraint.
    /// </summary>
    public const string ListenerReply = "ListenerReply";

    /// <summary>
    /// Reserved. See <see cref="ListenerReply"/>.
    /// </summary>
    public const string ArtistReply = "ArtistReply";

    /// <summary>
    /// The kinds a creator is permitted to send today. Used for input validation.
    /// </summary>
    public static readonly string[] CreatorSendable = [ThankYou];
}
