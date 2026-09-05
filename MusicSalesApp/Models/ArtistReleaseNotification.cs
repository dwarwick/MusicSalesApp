#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// One follower's notification that an artist they follow released a song. The row IS the in-app
/// notification; the email is an optional second delivery of the same thing, stamped on the row so
/// it cannot be sent twice.
/// </summary>
/// <remarks>
/// The unique index on (SongMetadataId, ListenerUserId) is the duplicate guard. It means the
/// creating job can be re-run, or run twice concurrently, without a listener hearing about the
/// same release more than once - which is why that job needs no bookkeeping of its own.
/// </remarks>
public class ArtistReleaseNotification
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The persona that released the song. Denormalised from the song so the listener's list can
    /// still name the artist after the song itself is removed.
    /// </summary>
    [Required]
    public int CreatorPersonaId { get; set; }

    [ForeignKey(nameof(CreatorPersonaId))]
    public CreatorPersona CreatorPersona { get; set; } = null!;

    [Required]
    public int SongMetadataId { get; set; }

    [ForeignKey(nameof(SongMetadataId))]
    public SongMetadata SongMetadata { get; set; } = null!;

    [Required]
    public int ListenerUserId { get; set; }

    [ForeignKey(nameof(ListenerUserId))]
    public ApplicationUser ListenerUser { get; set; } = null!;

    [Required]
    public DateTime CreatedDateUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ReadDateUtc { get; set; }

    /// <summary>
    /// When the email went out. Null means either "not yet" or "this listener has release emails
    /// switched off" - the email job decides which by looking at the listener's preference, so an
    /// unset value never causes a send to a listener who opted out.
    /// </summary>
    public DateTime? EmailSentDateUtc { get; set; }

    /// <summary>
    /// When push delivery was settled for this row. Same convention as
    /// <see cref="EmailSentDateUtc"/>: it means "the dispatcher is done with this", which includes
    /// deliberately skipping a listener who opted out or has no device registered.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT stamped when a send failed at the transport - no credentials, a Firebase
    /// outage, a timeout. Leaving it null is what makes the next run pick the row up again, and it
    /// is the reason this column is the one piece of push state that has to be a timestamp rather
    /// than a bool.
    /// </remarks>
    public DateTime? PushSentDateUtc { get; set; }
}
