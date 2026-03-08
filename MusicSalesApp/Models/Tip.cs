#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Possible states for a tip.
/// </summary>
public enum TipStatus
{
    /// <summary>Tip received, within 7-day hold period.</summary>
    Pending,
    /// <summary>Hold period passed, eligible for next payout.</summary>
    Cleared,
    /// <summary>Included in a payout to the creator.</summary>
    Paid,
    /// <summary>Refunded to the tipper.</summary>
    Refunded,
    /// <summary>PayPal chargeback received.</summary>
    Chargeback
}

/// <summary>
/// Represents a tip from a listener to a song creator.
/// Tips are held for at least 7 days before being included in a creator payout.
/// </summary>
public class Tip
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The user who sent the tip.
    /// </summary>
    public int TipperUserId { get; set; }

    [ForeignKey(nameof(TipperUserId))]
    public virtual ApplicationUser TipperUser { get; set; } = null!;

    /// <summary>
    /// The creator receiving the tip.
    /// </summary>
    public int CreatorId { get; set; }

    [ForeignKey(nameof(CreatorId))]
    public virtual Creator Creator { get; set; } = null!;

    /// <summary>
    /// Optional reference to the song being played when tip was given.
    /// </summary>
    public int? SongMetadataId { get; set; }

    [ForeignKey(nameof(SongMetadataId))]
    public virtual SongMetadata? SongMetadata { get; set; }

    /// <summary>
    /// Tip amount in USD. Minimum $1, maximum $50.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>
    /// Current status of the tip.
    /// </summary>
    public TipStatus Status { get; set; } = TipStatus.Pending;

    /// <summary>
    /// PayPal order ID from the checkout.
    /// </summary>
    [MaxLength(100)]
    public string PayPalOrderId { get; set; } = string.Empty;

    /// <summary>
    /// PayPal capture transaction ID from the capture response.
    /// This is the same value PayPal reports as seller_transaction_id in dispute webhooks.
    /// </summary>
    [MaxLength(100)]
    public string? PayPalCaptureId { get; set; }

    /// <summary>
    /// PayPal payout transaction ID when tip is paid to creator.
    /// </summary>
    [MaxLength(100)]
    public string? PayPalPayoutTransactionId { get; set; }

    /// <summary>
    /// IP address of the tipper for fraud detection.
    /// </summary>
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>
    /// Machine fingerprint of the tipper for fraud detection.
    /// </summary>
    [MaxLength(256)]
    public string? MachineFingerprint { get; set; }

    /// <summary>
    /// When the tip was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the PayPal order was captured (payment confirmed).
    /// Null means the tip was created but the buyer hasn't completed PayPal checkout yet.
    /// Only captured tips should be promoted to Cleared status.
    /// </summary>
    public DateTime? CapturedAt { get; set; }

    /// <summary>
    /// When the tip was paid out to the creator (if paid).
    /// </summary>
    public DateTime? PaidAt { get; set; }
}
