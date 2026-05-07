#nullable enable

using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

public class AdminMessage
{
    [Key]
    public int Id { get; set; }

    public int CreatedByUserId { get; set; }

    public virtual ApplicationUser CreatedByUser { get; set; } = default!;

    [Required]
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string MessageText { get; set; } = string.Empty;

    public bool SendEmail { get; set; }

    public bool ShowDialog { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CanceledAtUtc { get; set; }

    public int? CanceledByUserId { get; set; }

    public virtual ApplicationUser? CanceledByUser { get; set; }

    public virtual ICollection<AdminMessageRole> Roles { get; set; } = [];

    public virtual ICollection<AdminMessageRecipient> Recipients { get; set; } = [];
}