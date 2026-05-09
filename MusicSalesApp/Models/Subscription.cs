using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

public class Subscription
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [ForeignKey("UserId")]
    public ApplicationUser User { get; set; }

    [MaxLength(100)]
    public string PayPalSubscriptionId { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = SubscriptionStatuses.ApprovalPending;

    public DateTime StartDate { get; set; } = DateTime.UtcNow;

    public DateTime? EndDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MonthlyPrice { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CancelledAt { get; set; }

    public DateTime? LastPaymentDate { get; set; }

    public DateTime? NextBillingDate { get; set; }

    /// <summary>
    /// Billing source: "PayPal" or "GooglePlay". Defaults to PayPal for existing records.
    /// Use <see cref="BillingSources"/> constants.
    /// </summary>
    [MaxLength(20)]
    public string BillingSource { get; set; } = BillingSources.PayPal;

    /// <summary>
    /// Google Play purchase token used for server-side verification. Null for PayPal subscriptions.
    /// </summary>
    [MaxLength(500)]
    public string GooglePlayPurchaseToken { get; set; }

    /// <summary>
    /// Google Play order ID for the subscription. Null for PayPal subscriptions.
    /// </summary>
    [MaxLength(100)]
    public string GooglePlayOrderId { get; set; }

    /// <summary>
    /// Latest Apple App Store transaction ID for the subscription. Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(100)]
    public string AppStoreTransactionId { get; set; }

    /// <summary>
    /// Apple App Store original transaction ID that remains stable across renewals.
    /// Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(100)]
    public string AppStoreOriginalTransactionId { get; set; }

    /// <summary>
    /// Apple App Store product identifier for the subscription. Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(150)]
    public string AppStoreProductId { get; set; }

    /// <summary>
    /// Optional app account token supplied by the client to correlate App Store renewals back to a user.
    /// Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(100)]
    public string AppStoreAppAccountToken { get; set; }

    /// <summary>
    /// Apple environment for the subscription, for example Sandbox or Production.
    /// Null for non-Apple subscriptions.
    /// </summary>
    [MaxLength(20)]
    public string AppStoreEnvironment { get; set; }
}
