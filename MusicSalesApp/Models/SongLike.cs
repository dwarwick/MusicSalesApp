using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Represents a user's like or dislike for a song
/// </summary>
public class SongLike
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The user who liked/disliked the song
    /// </summary>
    [Required]
    public int UserId { get; set; }

    /// <summary>
    /// Navigation property to the user
    /// </summary>
    [ForeignKey(nameof(UserId))]
    public ApplicationUser User { get; set; } = null!;

    /// <summary>
    /// The song that was liked/disliked
    /// </summary>
    [Required]
    public int SongMetadataId { get; set; }

    /// <summary>
    /// Navigation property to the song metadata
    /// </summary>
    [ForeignKey(nameof(SongMetadataId))]
    public SongMetadata SongMetadata { get; set; } = null!;

    /// <summary>
    /// True for like, False for dislike
    /// </summary>
    [Required]
    public bool IsLike { get; set; }

    /// <summary>
    /// When this like/dislike was created
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this like/dislike was last updated
    /// </summary>
    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
