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
    public string Genre { get; set; } = string.Empty;
    public double? TrackLength { get; set; }
    public bool DisplayOnHomePage { get; set; }
    public bool IsAiGenerated { get; set; }
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
}
