#nullable enable

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

public class ContactRequestSubmission
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public virtual ApplicationUser User { get; set; } = null!;

    [Required]
    [MaxLength(256)]
    public string UserEmail { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Subject { get; set; } = string.Empty;

    public int MessageLength { get; set; }

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;

    public bool UserEmailSent { get; set; }

    public bool AdminEmailSent { get; set; }

    public DateTime? EmailSendCompletedAtUtc { get; set; }
}