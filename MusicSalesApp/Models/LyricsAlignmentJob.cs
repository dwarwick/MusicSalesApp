using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Models;

public enum LyricsAlignmentJobStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}

/// <summary>
/// One attempt to align a song's lyrics to its audio, from the moment the creator presses Submit
/// until timings are stored or the attempt gives up.
///
/// <para>
/// The lyrics counterpart of <see cref="SongUploadJob"/>, and it exists for the same reason: so
/// <see cref="Models.SongLyrics"/> is written only on success and nothing reading the catalogue has
/// to filter out a half-built result. It differs from that one in a way worth stating, because it
/// changes what the primary key means: an upload happens once per song, but a song can be re-aligned
/// as often as the creator likes. <see cref="JobId"/> is therefore the <em>attempt's</em> GUID, not
/// the song's media GUID - it names the staging folder holding this attempt's output and rides every
/// callback, and there can be many per song.
/// </para>
///
/// <para>
/// <see cref="DurableFunctionTaskId"/> is null between this row being written and the Hangfire
/// invoker successfully starting the orchestration. That gap is deliberate and load-bearing: the row
/// is written first so a start that never lands still leaves something for the reconciler to find,
/// and the invoker treats a non-null value as proof it has already run, which is what stops a
/// Hangfire retry from starting a second orchestration for the same attempt.
/// </para>
/// </summary>
public class LyricsAlignmentJob
{
    [Key]
    public int Id { get; set; }

    /// <summary>This attempt's GUID. Names its staging folder and correlates every callback.</summary>
    public Guid JobId { get; set; }

    public int SongMetadataId { get; set; }

    public virtual SongMetadata SongMetadata { get; set; }

    /// <summary>
    /// Whose song it is, carried so progress can be broadcast to the right SignalR group without
    /// re-reading the song row on every ping.
    ///
    /// <para>
    /// Deliberately a plain column with no foreign key, following the precedent
    /// <c>CoverArtMatchRequest</c> sets for a creator id that exists only to name a hub group. The
    /// alternative - a real FK - would put a second cascade path into this table alongside the one
    /// from <see cref="SongMetadata"/>, and buy nothing: the authoritative owner of an alignment
    /// attempt is the song, and the song already knows its creator.
    /// </para>
    /// </summary>
    public int CreatorId { get; set; }

    /// <summary>Null until the orchestration has actually been started. See the class remarks.</summary>
    public int? DurableFunctionTaskId { get; set; }

    public virtual DurableFunctionTask DurableFunctionTask { get; set; }

    /// <summary>
    /// Path within the media container of the lyrics this attempt was given. Required: the text is
    /// written before the row, so there is no state in which an attempt has no lyrics to align.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string LyricsBlobPath { get; set; }

    public LyricsAlignmentJobStatus Status { get; set; } = LyricsAlignmentJobStatus.Queued;

    /// <summary>
    /// How far the pipeline has got. Persisted rather than only broadcast, so a creator who closes
    /// the dialog and comes back picks the bar up in the right place, and so the reconciler can spot
    /// an attempt that has stopped moving rather than merely one that has been around a while.
    /// </summary>
    public LyricsAlignmentStep Step { get; set; } = LyricsAlignmentStep.Submitted;

    /// <summary>
    /// When <see cref="Step"/> last changed, or when a progress ping last proved the run was alive.
    /// Only step transitions and a once-a-minute liveness refresh touch this row; the far more
    /// frequent percentage updates ride SignalR alone.
    /// </summary>
    public DateTime StepUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The composite score the Function reported, 0-1. Recorded even when it falls below the
    /// publishing threshold - that is precisely the case someone will want to look at.
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>A constant from <c>LyricsAlignmentFailureCodes</c>.</summary>
    [MaxLength(100)]
    public string FailureCode { get; set; }

    [MaxLength(2000)]
    public string FailureMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}
