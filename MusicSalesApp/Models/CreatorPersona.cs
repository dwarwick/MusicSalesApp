#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Represents an artist persona created by a Creator.
/// Creators can have multiple personas (e.g., different band names or aliases).
/// When a song is linked to a persona, the persona name is shown in place of the creator name.
/// </summary>
public class CreatorPersona
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the Creator who owns this persona.
    /// </summary>
    public int CreatorId { get; set; }

    /// <summary>
    /// Navigation property to the Creator.
    /// </summary>
    [ForeignKey(nameof(CreatorId))]
    public virtual Creator Creator { get; set; } = null!;

    /// <summary>
    /// The display name of the persona (e.g., artist or band name). Required.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A biography or description for this persona.
    /// </summary>
    [MaxLength(2000)]
    public string? Bio { get; set; }

    /// <summary>
    /// Full path to the persona profile image in Azure Blob Storage (persona images container).
    /// The image should be square (1:1 aspect ratio).
    /// </summary>
    [MaxLength(500)]
    public string? ImageBlobPath { get; set; }

    /// <summary>
    /// Optional website URL for this persona.
    /// </summary>
    [MaxLength(500)]
    public string? WebsiteUrl { get; set; }

    /// <summary>
    /// The width of the persona image in pixels. Populated during upload or crop operations.
    /// </summary>
    public int? ImageWidth { get; set; }

    /// <summary>
    /// The height of the persona image in pixels. Populated during upload or crop operations.
    /// </summary>
    public int? ImageHeight { get; set; }

    /// <summary>
    /// When this persona was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this persona was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Returns true if the persona image is a perfect square, false if not square,
    /// or null if dimensions are unknown.
    /// </summary>
    [NotMapped]
    public bool? IsImageSquare =>
        (ImageWidth.HasValue && ImageHeight.HasValue)
            ? ImageWidth.Value == ImageHeight.Value
            : null;
}
