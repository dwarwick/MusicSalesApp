using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Represents a user-submitted report against a song for policy violations.
/// </summary>
public class ReportedSong
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The song being reported
    /// </summary>
    [Required]
    public int SongMetadataId { get; set; }

    [ForeignKey(nameof(SongMetadataId))]
    public SongMetadata SongMetadata { get; set; } = null!;

    /// <summary>
    /// The user who submitted the report
    /// </summary>
    [Required]
    public int ReportingUserId { get; set; }

    [ForeignKey(nameof(ReportingUserId))]
    public ApplicationUser ReportingUser { get; set; } = null!;

    /// <summary>
    /// The reason for the report. Must be a value from <see cref="Common.Helpers.ReportReasonTypes"/>.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// When the report was created
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the report was resolved (null if not yet resolved)
    /// </summary>
    public DateTime? ResolutionDateTime { get; set; }

    /// <summary>
    /// Resolution outcome: null = unresolved, true = accepted (song removed), false = rejected (song stays)
    /// </summary>
    public bool? ResolutionAccepted { get; set; }
}
