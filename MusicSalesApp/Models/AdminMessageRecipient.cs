using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

public class AdminMessageRecipient
{
    [Key]
    public int Id { get; set; }

    public int AdminMessageId { get; set; }

    public virtual AdminMessage AdminMessage { get; set; } = default!;

    public int UserId { get; set; }

    public virtual ApplicationUser User { get; set; } = default!;

    [Required]
    [MaxLength(256)]
    public string EmailAddressSnapshot { get; set; } = string.Empty;

    public DateTime? DialogDeliveredAtUtc { get; set; }

    public DateTime? EmailSentAtUtc { get; set; }

    public DateTime? AcknowledgedAtUtc { get; set; }

    public DateTime? CanceledAtUtc { get; set; }
}