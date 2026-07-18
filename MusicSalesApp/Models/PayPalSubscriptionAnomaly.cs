using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

#nullable enable

namespace MusicSalesApp.Models;

public sealed class PayPalSubscriptionAnomaly
{
    [Key]
    public int Id { get; set; }

    public int SubscriptionId { get; set; }

    [ForeignKey(nameof(SubscriptionId))]
    public Subscription Subscription { get; set; } = default!;

    public int UserId { get; set; }

    [Required, MaxLength(100)]
    public string PayPalSubscriptionId { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string ProviderStatus { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string LocalStatus { get; set; } = string.Empty;

    public DateTime? ProviderStartDate { get; set; }
    public DateTime? ProviderTrialEndDate { get; set; }
    public DateTime? ProviderLastPaymentDate { get; set; }
    public DateTime? ProviderNextBillingDate { get; set; }
    public DateTime? LocalTrialEndDate { get; set; }
    public DateTime? LocalLastPaymentDate { get; set; }
    public DateTime? LocalNextBillingDate { get; set; }
    public DateTime? LocalEndDate { get; set; }
    public int FailedPaymentsCount { get; set; }

    [MaxLength(1000)]
    public string? ReconciliationError { get; set; }

    [Required, MaxLength(36)]
    public string CorrelationId { get; set; } = string.Empty;

    public DateTime DetectedAtUtc { get; set; }
    public DateTime LastObservedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public DateTime? UserEmailSentAtUtc { get; set; }
    public DateTime? AdminEmailSentAtUtc { get; set; }

    [MaxLength(36)]
    public string? NotificationClaimId { get; set; }

    public DateTime? NotificationClaimedAtUtc { get; set; }
}
