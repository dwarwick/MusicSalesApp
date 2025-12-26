using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Stores the recommended playlist for a user based on their likes and other users' preferences.
/// This playlist is built once per day and stored for quick retrieval.
/// </summary>
public class RecommendedPlaylist
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The user this recommendation is for
    /// </summary>
    [Required]
    public int UserId { get; set; }

    /// <summary>
    /// Navigation property to the user
    /// </summary>
    [ForeignKey(nameof(UserId))]
    public ApplicationUser User { get; set; } = null!;

    /// <summary>
    /// The recommended song
    /// </summary>
    [Required]
    public int SongMetadataId { get; set; }

    /// <summary>
    /// Navigation property to the song metadata
    /// </summary>
    [ForeignKey(nameof(SongMetadataId))]
    public SongMetadata SongMetadata { get; set; } = null!;

    /// <summary>
    /// The order of this song in the recommended playlist (1-20)
    /// </summary>
    [Required]
    public int DisplayOrder { get; set; }

    /// <summary>
    /// When this recommendation was generated
    /// </summary>
    [Required]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The recommendation score (higher = more recommended)
    /// </summary>
    public double Score { get; set; }
}
