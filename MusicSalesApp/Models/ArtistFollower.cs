#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// A listener following an artist. The artist is a <see cref="CreatorPersona"/> rather than a
/// <see cref="Creator"/>, because the persona is the only stable artist identity StreamTunes has -
/// a song's displayed artist otherwise falls back through free text to a creator display name, and
/// a creator with two aliases should not have one follower list covering both.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unfollowing is a soft delete.</b> The row survives with <see cref="IsActive"/> false so that
/// <see cref="AnonymousListenerNumber"/> stays the same if the listener follows again later, and so
/// that a block outlives the follow it was made from. Nothing outside account deletion removes one.
/// </para>
/// <para>
/// The unique index on (CreatorPersonaId, ListenerUserId) is what makes "following the same artist
/// twice is impossible" a database guarantee rather than a service-layer intention.
/// </para>
/// </remarks>
public class ArtistFollower
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The persona being followed.
    /// </summary>
    [Required]
    public int CreatorPersonaId { get; set; }

    [ForeignKey(nameof(CreatorPersonaId))]
    public CreatorPersona CreatorPersona { get; set; } = null!;

    /// <summary>
    /// The listener doing the following. Never disclosed to the creator - see
    /// <see cref="AnonymousListenerNumber"/>.
    /// </summary>
    [Required]
    public int ListenerUserId { get; set; }

    [ForeignKey(nameof(ListenerUserId))]
    public ApplicationUser ListenerUser { get; set; } = null!;

    /// <summary>
    /// When the follow was first created. Re-following after an unfollow does NOT reset this;
    /// the creator's "Following Since" column is meant to show the start of the relationship.
    /// </summary>
    [Required]
    public DateTime FollowedDateUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The song the listener was looking at when they followed, when there was one. Powers the
    /// creator's "Followed After Listening To" column - useful analytics that says nothing about
    /// who the listener is.
    /// </summary>
    public int? SourceSongMetadataId { get; set; }

    [ForeignKey(nameof(SourceSongMetadataId))]
    public SongMetadata? SourceSongMetadata { get; set; }

    /// <summary>
    /// False once the listener unfollows. See the soft-delete note on the class.
    /// </summary>
    [Required]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When the listener last unfollowed, or null if they never have.
    /// </summary>
    public DateTime? UnfollowedDateUtc { get; set; }

    /// <summary>
    /// Per-artist mute for new-release notifications. Lets a listener stay a follower while going
    /// quiet, which is the alternative to unfollowing that the feature is supposed to offer.
    /// </summary>
    [Required]
    public bool ReleaseNotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Per-artist mute for messages from the artist.
    /// </summary>
    [Required]
    public bool ArtistMessagesEnabled { get; set; } = true;

    /// <summary>
    /// The listener has blocked this artist. Stronger than a mute: it suppresses everything and
    /// prevents the creator initiating any new contact, and it deliberately survives an unfollow
    /// so that re-following is not a way back in.
    /// </summary>
    [Required]
    public bool IsBlockedByListener { get; set; }

    public DateTime? BlockedDateUtc { get; set; }

    /// <summary>
    /// The pseudonym this listener wears for THIS artist, rendered as "Listener #4817".
    /// </summary>
    /// <remarks>
    /// Assigned at random within the persona rather than derived from the listener's user id, and
    /// stored so it never changes. Two properties follow from that and both matter: a creator
    /// cannot work backwards to an account, and two creators comparing notes cannot tell that the
    /// same person follows them both. A keyed hash of the user id would satisfy neither once the
    /// key leaked, and a sequential number would additionally leak the order people followed in.
    /// </remarks>
    [Required]
    public int AnonymousListenerNumber { get; set; }

    /// <summary>
    /// Which of the follower's own personas they chose to be seen as for THIS follow, or null to
    /// follow anonymously.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set only when the follower is a creator who has switched
    /// <see cref="Creator.RevealPersonaToFollowedArtists"/> on. It records a choice, not a
    /// permission: the name is shown only if that consent is STILL on when the list is read, so
    /// this value alone never reveals anything.
    /// </para>
    /// <para>
    /// A stored choice is what makes the two directions asymmetric, on purpose. Switching consent
    /// off hides every follow immediately; switching it on names nothing retroactively, because
    /// follows made anonymously carry no choice here. Hiding should be automatic, revealing should
    /// take a deliberate act.
    /// </para>
    /// <para>
    /// <b>Deliberately not a foreign key.</b> ArtistFollower already cascades from CreatorPersona
    /// through <see cref="CreatorPersonaId"/>, and a second relationship to the same table would be
    /// a second cascade path, which SQL Server refuses - leaving NoAction, which would make
    /// deleting a persona fail unless every one of these were cleared first. A plain id degrades
    /// the right way instead: the display query only names a persona that still exists and is
    /// still enabled, so a deleted one silently reverts the follow to anonymous.
    /// </para>
    /// </remarks>
    public int? FollowAsPersonaId { get; set; }
}
