using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Stores metadata for songs and albums that was previously stored in Azure Blob index tags
/// </summary>
public class SongMetadata
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Full path to the blob file (folder/filename) - DEPRECATED: Use Mp3BlobPath or ImageBlobPath instead
    /// </summary>
    [MaxLength(500)]
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the MP3 blob file (folder/filename)
    /// </summary>
    [MaxLength(500)]
    public string Mp3BlobPath { get; set; }

    /// <summary>
    /// Full path to the image blob file (folder/filename)
    /// </summary>
    [MaxLength(500)]
    public string ImageBlobPath { get; set; }

    /// <summary>
    /// File extension (.mp3, .jpg, .jpeg, .png) - DEPRECATED
    /// </summary>
    [MaxLength(10)]
    public string FileExtension { get; set; } = string.Empty;

    /// <summary>
    /// The name of the album that this file belongs to
    /// </summary>
    [MaxLength(200)]
    public string AlbumName { get; set; }

    /// <summary>
    /// Indicates whether this image file is the cover art for an album
    /// </summary>
    public bool IsAlbumCover { get; set; }

    /// <summary>
    /// The price of an album (for album cover images)
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? AlbumPrice { get; set; }

    /// <summary>
    /// The price of a song (for MP3 files)
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? SongPrice { get; set; }

    /// <summary>
    /// The genre of the song (e.g., Rock, Country, Pop)
    /// </summary>
    [MaxLength(50)]
    public string Genre { get; set; }

    /// <summary>
    /// The track number for an album track (1-based index)
    /// </summary>
    public int? TrackNumber { get; set; }

    /// <summary>
    /// The track length in seconds
    /// </summary>
    public double? TrackLength { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The number of times this song has been streamed (played for at least 30 seconds)
    /// </summary>
    public int NumberOfStreams { get; set; }

    /// <summary>
    /// Indicates whether this song or album should be displayed on the home page
    /// </summary>
    public bool DisplayOnHomePage { get; set; }

    /// <summary>
    /// Indicates whether this song is active and available for playback.
    /// Inactive songs are not displayed anywhere on the website and cannot be played.
    /// Songs are set to inactive when a seller deletes them or closes their account.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Foreign key to the Seller who uploaded this song.
    /// If null, the song was uploaded by the platform admin.
    /// </summary>
    public int? SellerId { get; set; }

    /// <summary>
    /// Navigation property to the Seller who owns this song.
    /// </summary>
    public virtual Seller Seller { get; set; }
}
