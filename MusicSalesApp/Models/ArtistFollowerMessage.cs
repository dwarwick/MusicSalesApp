#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

/// <summary>
/// A message sent inside a follow relationship. In version 1 there is exactly one kind - the
/// artist's thank-you - and listeners cannot reply.
/// </summary>
/// <remarks>
/// <para>
/// The message hangs off the <see cref="ArtistFollower"/> row rather than off a pair of user ids,
/// which is what keeps the two parties from ever needing each other's identity: the relationship
/// is the address.
/// </para>
/// <para>
/// A filtered unique index on (ArtistFollowerId) WHERE MessageKind = 'ThankYou' enforces
/// one-thank-you-per-follower in the schema. <see cref="MessageKind"/> exists so that adding
/// replies later is a new value rather than a dropped constraint.
/// </para>
/// </remarks>
public class ArtistFollowerMessage
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The follow relationship this message belongs to.
    /// </summary>
    [Required]
    public int ArtistFollowerId { get; set; }

    [ForeignKey(nameof(ArtistFollowerId))]
    public ArtistFollower ArtistFollower { get; set; } = null!;

    /// <summary>
    /// The account that sent it, kept for audit and moderation.
    /// </summary>
    /// <remarks>
    /// <b>This must never reach a listener-facing DTO.</b> A listener sees the persona name and
    /// nothing else; the privacy promise runs in both directions, so the human behind an alias is
    /// no more disclosed than the listener behind a pseudonym.
    /// </remarks>
    [Required]
    public int SenderUserId { get; set; }

    [ForeignKey(nameof(SenderUserId))]
    public ApplicationUser SenderUser { get; set; } = null!;

    /// <summary>
    /// One of <see cref="ArtistMessageKinds"/>.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string MessageKind { get; set; } = ArtistMessageKinds.ThankYou;

    /// <summary>
    /// The message body, already normalised and validated by
    /// <see cref="ArtistMessageContentPolicy"/>. The column is sized to that policy's limit.
    /// </summary>
    [Required]
    [MaxLength(ArtistMessageContentPolicy.MaxLength)]
    public string MessageText { get; set; } = string.Empty;

    [Required]
    public DateTime CreatedDateUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the listener read it, or null while unread.
    /// </summary>
    public DateTime? ReadDateUtc { get; set; }

    /// <summary>
    /// When the notification email went out, or null if it has not been sent yet or the listener
    /// has message emails switched off.
    /// </summary>
    public DateTime? EmailSentDateUtc { get; set; }

    /// <summary>
    /// When push delivery was settled. See the note on
    /// <see cref="ArtistReleaseNotification.PushSentDateUtc"/> - a transport failure leaves this
    /// null on purpose so the row is retried.
    /// </summary>
    public DateTime? PushSentDateUtc { get; set; }

    /// <summary>
    /// An optional song the message is about, so a thank-you can be tied to what prompted it.
    /// </summary>
    public int? RelatedSongMetadataId { get; set; }

    [ForeignKey(nameof(RelatedSongMetadataId))]
    public SongMetadata? RelatedSongMetadata { get; set; }

    /// <summary>
    /// The listener has hidden it from their own list. Not a delete: a reported message has to
    /// survive for an admin to review, and hiding is the listener's half of that.
    /// </summary>
    [Required]
    public bool IsHiddenByListener { get; set; }

    [Required]
    public bool IsReported { get; set; }

    /// <summary>
    /// One of <see cref="ReportReasonTypes"/> when reported.
    /// </summary>
    [MaxLength(100)]
    public string? ReportReason { get; set; }

    public DateTime? ReportedAtUtc { get; set; }

    /// <summary>
    /// When an admin closed the report; null while it sits in the review queue.
    /// </summary>
    public DateTime? ModerationResolvedAtUtc { get; set; }

    /// <summary>
    /// Null while unresolved, true when the report was upheld, false when it was rejected.
    /// Matches the shape of <see cref="ReportedSong.ResolutionAccepted"/>.
    /// </summary>
    public bool? ModerationAccepted { get; set; }
}
