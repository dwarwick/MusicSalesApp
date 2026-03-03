using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

#nullable enable

namespace MusicSalesApp.Models;

/// <summary>
/// Tracks the history of user-related events for admin auditing purposes.
/// </summary>
public class UserHistory
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The user ID associated with this event.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    public virtual ApplicationUser User { get; set; } = default!;

    /// <summary>
    /// The email of the user at the time of the event.
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string UserEmail { get; set; } = string.Empty;

    /// <summary>
    /// The type of event that occurred (e.g., "Registration", "EmailConfirmed", "CreatorStatusChanged").
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// A human-readable description of what happened.
    /// </summary>
    [Required]
    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The original value before the change (if applicable).
    /// </summary>
    [MaxLength(500)]
    public string? OldValue { get; set; }

    /// <summary>
    /// The new value after the change (if applicable).
    /// </summary>
    [MaxLength(500)]
    public string? NewValue { get; set; }

    /// <summary>
    /// The date and time when this event occurred (UTC).
    /// </summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
