#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Immutable audit log for PayPal chargeback/dispute events.
/// Each CUSTOMER.DISPUTE.CREATED webhook creates one record.
/// </summary>
public class ChargebackLog
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Unique dispute identifier from PayPal (e.g., PP-D-12345).
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string PayPalDisputeId { get; set; } = string.Empty;

    /// <summary>
    /// The subscriber or tipper involved (nullable if user not found).
    /// </summary>
    public int? UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public virtual ApplicationUser? User { get; set; }

    /// <summary>
    /// Email of the subscriber or tipper at the time of the dispute.
    /// </summary>
    [MaxLength(256)]
    public string? UserEmail { get; set; }

    /// <summary>
    /// PayPal subscription ID (null for tip chargebacks).
    /// </summary>
    [MaxLength(100)]
    public string? PayPalSubscriptionId { get; set; }

    /// <summary>
    /// Tip record FK (null for subscription chargebacks).
    /// </summary>
    public int? TipId { get; set; }

    [ForeignKey(nameof(TipId))]
    public virtual Tip? Tip { get; set; }

    /// <summary>
    /// The PayPal capture/transaction ID that was disputed.
    /// </summary>
    [MaxLength(100)]
    public string? SellerTransactionId { get; set; }

    /// <summary>
    /// Dispute reason (e.g., UNAUTHORISED, MERCHANDISE_OR_SERVICE_NOT_RECEIVED).
    /// </summary>
    [MaxLength(200)]
    public string? Reason { get; set; }

    /// <summary>
    /// Lifecycle stage (INQUIRY, CHARGEBACK, PRE_ARBITRATION, ARBITRATION).
    /// </summary>
    [MaxLength(50)]
    public string? Stage { get; set; }

    /// <summary>
    /// INTERNAL (PayPal dispute) or EXTERNAL (bank/card chargeback).
    /// </summary>
    [MaxLength(50)]
    public string? Channel { get; set; }

    /// <summary>
    /// Disputed amount with currency (e.g., "3.99 USD").
    /// </summary>
    [MaxLength(50)]
    public string? Amount { get; set; }

    /// <summary>
    /// Processing outcome: PROCESSED_SUBSCRIPTION, PROCESSED_TIP, NO_TRANSACTION_FOUND, or ERROR.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Additional context (e.g., refund failure details, payout reversal warnings).
    /// </summary>
    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>
    /// UTC timestamp when the webhook was received.
    /// </summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
