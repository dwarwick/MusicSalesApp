using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

#nullable enable

namespace MusicSalesApp.Models;

/// <summary>
/// Application settings stored in the database for runtime configuration.
/// </summary>
public class AppSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Unique key identifying the setting (e.g., "SubscriptionPrice").
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Value of the setting stored as a string.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of what this setting controls.
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// When the setting was last modified.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
