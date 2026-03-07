#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Records a tip attempt that was blocked by a fraud detection rule.
/// Used for admin audit and investigation.
/// </summary>
public class BlockedTipAttempt
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The user who attempted the tip.
    /// </summary>
    public int TipperUserId { get; set; }

    [ForeignKey(nameof(TipperUserId))]
    public virtual ApplicationUser TipperUser { get; set; } = null!;

    /// <summary>
    /// The creator who would have received the tip.
    /// </summary>
    public int CreatorId { get; set; }

    [ForeignKey(nameof(CreatorId))]
    public virtual Creator Creator { get; set; } = null!;

    /// <summary>
    /// The attempted tip amount in USD.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>
    /// The fraud rule that blocked the tip (e.g., "FingerprintFraud", "IpFraud", "ReciprocalTipping", etc.).
    /// </summary>
    [MaxLength(100)]
    public string FraudRule { get; set; } = string.Empty;

    /// <summary>
    /// The human-readable reason the tip was blocked.
    /// </summary>
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// IP address of the tipper at the time of the attempt.
    /// </summary>
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>
    /// Machine fingerprint of the tipper at the time of the attempt.
    /// </summary>
    [MaxLength(256)]
    public string? MachineFingerprint { get; set; }

    /// <summary>
    /// When the blocked attempt occurred.
    /// </summary>
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
}
