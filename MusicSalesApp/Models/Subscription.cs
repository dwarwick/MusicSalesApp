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
}
