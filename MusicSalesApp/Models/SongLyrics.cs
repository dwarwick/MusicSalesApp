using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

/// <summary>
/// What a song's lyrics are currently worth to a listener.
/// </summary>
public enum SongLyricsStatus
{
    /// <summary>Text stored, alignment queued or running. No timings yet.</summary>
    Pending = 0,

    /// <summary>
    /// Timings cleared the confidence threshold. <b>The only status a player may read from.</b>
    /// </summary>
    Published = 1,

    /// <summary>
    /// Timings exist but did not clear the threshold, or failed a structural check that reviewing
    /// could plausibly fix. Kept and shown to the creator, withheld from listeners.
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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
