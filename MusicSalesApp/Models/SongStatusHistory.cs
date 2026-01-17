using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Tracks the history of song enable/disable actions for auditing purposes.
/// </summary>
public class SongStatusHistory
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the SongMetadata record.
    /// </summary>
    public int SongMetadataId { get; set; }

    /// <summary>
    /// Navigation property to the song metadata.
    /// </summary>
    public virtual SongMetadata SongMetadata { get; set; }

    /// <summary>
    /// Indicates whether the song was enabled (true) or disabled (false) by this action.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// The reason provided for enabling or disabling the song.
    /// </summary>
    [Required]
    [MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// The date and time when this status change occurred.
    /// </summary>
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The user ID of the admin who made this change (optional for system changes).
    /// </summary>
    public int? ChangedByUserId { get; set; }

    /// <summary>
    /// Navigation property to the admin user who made the change.
    /// </summary>
    public virtual ApplicationUser ChangedByUser { get; set; }
}
