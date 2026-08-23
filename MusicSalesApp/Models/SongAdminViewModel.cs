namespace MusicSalesApp.Models;

/// <summary>
/// View model for the admin song management grid
/// </summary>
public class SongAdminViewModel
{
    public string Id { get; set; } = string.Empty; // Unique identifier (blob name)
    public string SongImageUrl { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string Mp3FileName { get; set; } = string.Empty;
    public string JpegFileName { get; set; } = string.Empty;
    public string OriginalAudioFileName { get; set; } = string.Empty;

    /// <summary>
    /// The song's recorded cover-art rendition widths, carried here so the grid can request a
    /// thumbnail-sized image instead of the full-size master without a second database round trip.
    /// </summary>
    public string CoverArtVariantWidths { get; set; } = string.Empty;

    /// <summary>
    /// Identifies this song's media in blob storage; null for songs that predate the GUID naming
    /// scheme. Carried here so the crop and cover-art-replace flows can derive their target path
    /// without a second database round trip.
    /// </summary>
    public Guid? MediaGuid { get; set; }
    public long? OriginalAudioFileSize { get; set; }
    public string OriginalAudioContentType { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public double? TrackLength { get; set; }
    public bool DisplayOnHomePage { get; set; }
    public bool IsAiGenerated { get; set; }
    public bool IsAiVocals { get; set; }
    public bool IsAiLyrics { get; set; }
    public int NumberOfStreams { get; set; }

    /// <summary>
    /// The number of likes this song has received.
    /// </summary>
    public int LikeCount { get; set; }

    // Creator-related properties
    public int? CreatorId { get; set; }
    public bool IsActive { get; set; } = true;

    // Enable/Disable properties
    public bool IsEnabled { get; set; } = true;
    public string StatusReason { get; set; } = string.Empty;

    /// <summary>
    /// The effective artist name for display (derived from ArtistName > DisplayName > Email).
    /// </summary>
    public string ArtistName { get; set; } = string.Empty;

    /// <summary>
    /// The creator's email address for admin reference.
    /// </summary>
    public string CreatorEmail { get; set; } = string.Empty;

    /// <summary>
    /// The raw artist name from the SongMetadata.ArtistName field for editing purposes.
    /// </summary>
    public string RawArtistName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the song's cover image is a perfect square.
    /// Null means dimensions are unknown.
    /// </summary>
    public bool? IsImageSquare { get; set; }

    /// <summary>
    /// The ID of the persona associated with this song (if any).
    /// </summary>
    public int? PersonaId { get; set; }

    /// <summary>
    /// The name of the persona associated with this song (if any).
    /// </summary>
    public string PersonaName { get; set; } = string.Empty;

    /// <summary>
    /// The SAS URL of the persona image (if any).
    /// </summary>
    public string PersonaImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// The album name of the song (for album tracks).
    /// </summary>
    public string AlbumName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this entry is an album (has an album name).
    /// </summary>
    public bool IsAlbum { get; set; }

    /// <summary>
    /// The song's lyric timing state, or null if lyrics have never been submitted for it.
    /// </summary>
    public SongLyricsStatus? LyricsStatus { get; set; }

    /// <summary>
    /// Where this song's Enhanced LRC lives, or empty if there is not one to download.
    ///
    /// <para>
    /// Empty even for a song whose timings landed: the LRC copy is allowed to fail on its own
    /// without costing the timings (see <c>LyricsAlignmentCompletionService</c>), so "the alignment
    /// succeeded" and "there is a file to hand the creator" are two different questions. The grid
    /// offers Download LRC only when this is set, and falls back to the Lyrics button otherwise -
    /// a download button that silently does nothing is worse than not offering one.
    /// </para>
    /// </summary>
    public string LyricsLrcBlobPath { get; set; } = string.Empty;

    /// <summary>
    /// When an administrator took this song's lyrics down, or null if they are the creator's to
    /// control. Admin surfaces only.
    /// </summary>
    public DateTime? LyricsDisabledAt { get; set; }

    /// <summary>
    /// Whether the creator has timing edits they have saved but not released.
    /// </summary>
    /// <remarks>
    /// Worth a badge because the draft split makes it genuinely invisible otherwise: the song still
    /// plays, the lyrics still show the published version, and nothing anywhere says there is newer
    /// work sitting behind them. A creator interrupted mid-session has no other way to find out.
    /// </remarks>
    public bool HasUnpublishedLyricEdits { get; set; }
}
