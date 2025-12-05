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
    /// Full path to the blob file (folder/filename)
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>
    /// File extension (.mp3, .jpg, .jpeg)
    /// </summary>
    [Required]
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
}
