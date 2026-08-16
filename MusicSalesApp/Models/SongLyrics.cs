using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// What a song's lyrics are currently worth to a listener.
/// </summary>
public enum SongLyricsStatus
{
    /// <summary>Text stored, alignment queued or running. No timings yet.</summary>
    Pending = 0,

    /// <summary>
    /// The creator has heard these timings and released them. <b>The only status a player may read
    /// from</b>, and the only one a creator can put a song into.
    ///
    /// <para>
    /// Alignment never produces this. It is reached solely by a creator pressing Publish in the
    /// timing editor, because machine alignment of sung vocals is 150-300 ms out on a good day and a
    /// listener notices immediately.
    /// </para>
    /// </summary>
    Published = 1,

    /// <summary>
    /// Timings exist and are waiting for the creator to hear them. Kept, shown to the creator,
    /// withheld from listeners.
    ///
    /// <para>
    /// Every successful alignment lands here, whatever it scored. The confidence figure decides only
    /// how the creator is greeted - "have a listen" against "expect to do some tapping" - so the
    /// state does not carry a verdict that an admin moving the threshold would leave stale.
    /// </para>
    /// </summary>
    NeedsReview = 2,

    /// <summary>No usable timings were produced. The pasted text is retained regardless.</summary>
    Failed = 3
}

/// <summary>
/// A song's lyrics and, once alignment succeeds, its word-level karaoke timings.
///
/// <para>
/// One row per song, unlike <see cref="LyricsAlignmentJob"/>, which is one row per attempt. This is
/// the published state; that is the history of how it got there. The split matters because
/// re-alignment is a first-class operation - the artist fixes a typo, re-runs, and this row is
/// updated in place rather than accumulating.
/// </para>
///
/// <para>
/// The blob paths are fixed rather than versioned, so a re-alignment overwrites the previous
/// timings. <see cref="Version"/> is the cache-buster instead, exactly as
/// <c>SongMetadata.CoverArtVariantVersion</c> is for cover art that a re-crop overwrites in place.
/// Versioning the paths would be the obvious alternative and is the wrong one: the media account
/// deliberately has no lifecycle rule, because its prefixes are the live catalogue, so every
/// superseded version would sit there forever with nothing to sweep it.
/// </para>
///
/// <para>
/// One consequence to leave alone: the JSON and the LRC are two blobs written in sequence, so there
/// is a sub-second window during a re-alignment where one is new and the other is old. That is
/// acceptable - the JSON is the primary artifact and the LRC is an export convenience - and it is
/// deliberately not solved with a two-phase write, which would cost far more than the window is
/// worth.
/// </para>
/// </summary>
public class SongLyrics
{
    [Key]
    public int Id { get; set; }

    /// <summary>Unique: a song has at most one lyrics record.</summary>
    public int SongMetadataId { get; set; }

    public virtual SongMetadata SongMetadata { get; set; }

    /// <summary>Path within the media container of the text the creator pasted.</summary>
    [MaxLength(500)]
    public string LyricsBlobPath { get; set; }

    /// <summary>Path within the media container of the word timings. Null until an alignment lands.</summary>
    [MaxLength(500)]
    public string TimingsBlobPath { get; set; }

    /// <summary>Path within the media container of the Enhanced LRC. Null until an alignment lands.</summary>
    [MaxLength(500)]
    public string LrcBlobPath { get; set; }

    public SongLyricsStatus Status { get; set; } = SongLyricsStatus.Pending;

    /// <summary>The composite score behind the current timings, 0-1. Null while pending.</summary>
    public double? Confidence { get; set; }

    /// <summary>
    /// Incremented on each successful alignment. Appended to the timings URL as a cache-buster,
    /// because the blob path itself never changes.
    /// </summary>
    public int Version { get; set; }

    /// <summary>When the current timings were produced. Null until the first success.</summary>
    public DateTime? AlignedAt { get; set; }

    /// <summary>
    /// The attempt that produced the current state, successful or not. Lets the creator's dialog
    /// show the right failure message without scanning the job table.
    /// </summary>
    public Guid? LastJobId { get; set; }

    /// <summary>
    /// Path within the media container of the creator's work-in-progress timings, if they have any.
    ///
    /// <para>
    /// <b>A separate blob from <see cref="TimingsBlobPath"/> so a song that is already live keeps
    /// serving its published timings untouched while its creator experiments.</b> Without the split,
    /// a creator halfway through re-tapping a chorus would be broadcasting that half-finished state
    /// to every listener, with no way back except re-running the whole alignment.
    /// </para>
    ///
    /// <para>
    /// Deliberately a path rather than the JSON itself: the document is tens of kilobytes, and
    /// <c>GetForSongsAsync</c> materialises whole entities to render the creator's song grid.
    /// </para>
    /// </summary>
    [MaxLength(500)]
    public string DraftTimingsBlobPath { get; set; }

    /// <summary>When the creator last saved a draft. Null if they have never edited these timings.</summary>
    public DateTime? DraftUpdatedAt { get; set; }

    /// <summary>When the creator last released timings to listeners. Null if they never have.</summary>
    /// <remarks>
    /// Compared against <see cref="DraftUpdatedAt"/> to answer "does this song have unpublished
    /// changes?", which is why neither is a boolean: a flag would have to be cleared correctly on
    /// every publish, discard and re-alignment, whereas a pair of timestamps cannot fall out of step.
    /// </remarks>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Whether the creator has edits they have not released yet.</summary>
    /// <remarks>Derived from the two timestamps, so there is no column behind it.</remarks>
    [NotMapped]
    public bool HasUnpublishedChanges =>
        DraftUpdatedAt.HasValue && (!PublishedAt.HasValue || DraftUpdatedAt > PublishedAt);

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
