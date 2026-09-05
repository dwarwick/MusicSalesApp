#nullable enable
namespace MusicSalesApp.Models;

// Data-transfer shapes for the artist follow feature.
//
// The split between the creator-facing and listener-facing records below is the privacy boundary
// made structural. ArtistFollowerSummaryDto has no field that could hold an email, a username or a
// listener id, so a query that tried to leak one would not compile - a far better guarantee than
// remembering not to select the column. ArtistMessageDto is the same idea in the other direction:
// it has nowhere to put the sending creator's account.

/// <summary>
/// One follower as their artist sees them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisplayName"/> is a pseudonym ("Listener #4817") for an ordinary listener, and the
/// follower's own public artist name when the follower is themselves an active creator - see
/// <see cref="IsIdentifiedArtist"/>. Either way it is the ONLY thing about the person this record
/// can carry: there is still no field able to hold an email, a username or a listener id.
/// </para>
/// </remarks>
public sealed record ArtistFollowerSummaryDto(
    int ArtistFollowerId,
    string DisplayName,
    bool IsIdentifiedArtist,
    DateTime FollowedDateUtc,
    int? SourceSongMetadataId,
    string? SourceSongTitle,
    bool HasBeenThanked,
    DateTime? LastMessageDateUtc,
    string? LastMessageText);

/// <summary>
/// One artist as their follower sees them.
/// </summary>
public sealed record FollowedArtistDto(
    int ArtistFollowerId,
    int CreatorPersonaId,
    string ArtistName,
    string? PersonaImageBlobPath,
    DateTime FollowedDateUtc,
    int? LatestReleaseSongMetadataId,
    string? LatestReleaseTitle,
    DateTime? LatestReleaseDateUtc,
    bool ReleaseNotificationsEnabled,
    bool ArtistMessagesEnabled,
    bool IsBlocked,
    int UnreadMessageCount);

/// <summary>
/// A message from an artist, as the listener sees it.
/// </summary>
/// <remarks>
/// There is deliberately no sender user id, sender email or creator name on this record. The
/// listener knows the persona; the human behind the alias is no more disclosed to them than the
/// listener is to the creator.
/// </remarks>
public sealed record ArtistMessageDto(
    int MessageId,
    int CreatorPersonaId,
    string ArtistName,
    string MessageText,
    int? RelatedSongMetadataId,
    string? RelatedSongTitle,
    DateTime CreatedDateUtc,
    bool IsRead,
    bool IsReported);

/// <summary>
/// A new-release notification, as the listener sees it.
/// </summary>
public sealed record ArtistReleaseNotificationDto(
    int NotificationId,
    int CreatorPersonaId,
    string ArtistName,
    int SongMetadataId,
    string SongTitle,
    bool SongIsAvailable,
    DateTime CreatedDateUtc,
    bool IsRead);

/// <summary>
/// The creator's follower headline figures.
/// </summary>
public sealed record ArtistFollowerAnalyticsDto(
    int TotalFollowers,
    int NewFollowersThisMonth,
    IReadOnlyList<FollowSourceSongDto> TopSongsGeneratingFollows);

/// <summary>
/// The identities a listener may follow as, and whether they have consented to being named at all.
/// </summary>
/// <param name="RevealsPersona">
/// The creator's standing consent. False for anyone who is not a creator, and false by default for
/// those who are - in which case the follow is anonymous and no choice is offered.
/// </param>
/// <param name="Personas">
/// The follower's own enabled personas, for the "Follow as" dialog. Only meaningful when
/// <paramref name="RevealsPersona"/> is true.
/// </param>
public sealed record FollowAsOptionsDto(
    bool RevealsPersona,
    IReadOnlyList<FollowAsPersonaDto> Personas)
{
    /// <summary>
    /// True when the listener has a genuine choice to make. One persona needs no dialog, and
    /// neither does no consent.
    /// </summary>
    public bool NeedsChoice => RevealsPersona && Personas.Count > 1;

    /// <summary>
    /// The identity to use without asking: the only persona when there is exactly one, otherwise
    /// null for anonymous.
    /// </summary>
    public int? DefaultPersonaId =>
        RevealsPersona && Personas.Count == 1 ? Personas[0].Id : null;
}

/// <summary>One of the follower's own personas, as offered in the "Follow as" dialog.</summary>
public sealed record FollowAsPersonaDto(int Id, string Name);

/// <summary>
/// How many follows one song is credited with starting.
/// </summary>
public sealed record FollowSourceSongDto(int SongMetadataId, string SongTitle, int FollowCount);

/// <summary>
/// What happened when a follow state was requested.
/// </summary>
/// <remarks>
/// Split finely because the mobile client maps outcomes to HTTP status codes, and the distinction
/// between "your request was already true" and "we refused" decides whether a queued offline
/// intent is dropped or retried.
/// </remarks>
public enum ArtistFollowOutcome
{
    /// <summary>A new follow was created, or a dormant one reactivated.</summary>
    Followed,

    /// <summary>Already following; nothing changed.</summary>
    AlreadyFollowing,

    /// <summary>The follow was deactivated.</summary>
    Unfollowed,

    /// <summary>Not following in the first place; nothing changed.</summary>
    NotFollowing,

    /// <summary>The persona does not exist, is disabled, or its creator is inactive or suspended.</summary>
    ArtistUnavailable,

    /// <summary>The listener has blocked this artist and must unblock before following again.</summary>
    Blocked,

    /// <summary>
    /// The persona belongs to the caller. Following yourself is meaningless, and it would put the
    /// creator in their own follower list and their own analytics.
    /// </summary>
    CannotFollowSelf,
}

/// <summary>
/// What happened when a creator tried to send a thank-you.
/// </summary>
public enum ArtistThankYouOutcome
{
    Sent,

    /// <summary>This follower has already been thanked. One per relationship, ever.</summary>
    AlreadyThanked,

    /// <summary>The text failed <c>ArtistMessageContentPolicy</c>; see the reason on the result.</summary>
    ContentRejected,

    /// <summary>The listener is not currently following, so the creator may not initiate contact.</summary>
    NotFollowing,

    /// <summary>The listener has blocked or muted this artist.</summary>
    Blocked,

    /// <summary>The persona's daily thank-you allowance is used up.</summary>
    RateLimited,

    /// <summary>The persona is disabled, or its creator is inactive or suspended.</summary>
    ArtistUnavailable,

    /// <summary>The signed-in creator does not own the persona they are sending as.</summary>
    NotPersonaOwner,
}

/// <summary>
/// The outcome of a thank-you attempt, with the reason text when the content was refused.
/// </summary>
public sealed record ArtistThankYouResult(ArtistThankYouOutcome Outcome, string? RejectionReason = null)
{
    public bool Succeeded => Outcome == ArtistThankYouOutcome.Sent;
}
