using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

/// <summary>
/// Represents a music genre that can be assigned to songs.
/// </summary>
public class Genre
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The name of the genre (e.g., Rock, Pop, Jazz).
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this genre is active and available for selection.
    /// Disabled genres are hidden from dropdowns.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When this genre was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The email address of the user who added this genre.
    /// </summary>
    [MaxLength(256)]
    public string CreatedByEmail { get; set; } = string.Empty;
}
