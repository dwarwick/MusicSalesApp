namespace MusicSalesApp.Models;

/// <summary>
/// Provider-confirmed PayPal subscription state to persist as one atomic update.
/// Null optional values leave the corresponding local value unchanged.
/// </summary>
public sealed class PayPalSubscriptionReconciliation
{
    public string Status { get; init; }

    public string PlanId { get; init; }

    public DateTime? ProviderStartDate { get; init; }

    public DateTime? TrialStartDate { get; init; }

    public DateTime? TrialEndDate { get; init; }

    public DateTime? LastPaymentDate { get; init; }

    public DateTime? NextBillingDate { get; init; }

    public int FailedPaymentsCount { get; init; }

    public decimal? MonthlyPrice { get; init; }

    public string CurrencyCode { get; init; }
}

/// <summary>
/// The persisted PayPal state plus transition information for idempotent follow-up work.
/// </summary>
public sealed class PayPalSubscriptionReconciliationResult
{
    public Subscription Subscription { get; init; }

    public string PreviousStatus { get; init; }

    public DateTime? PreviousLastPaymentDate { get; init; }

    public bool BecameActive { get; init; }

    public bool BecamePaid { get; init; }

    public bool TrialConverted { get; init; }

    public bool ShouldSendTrialActivationEmail { get; init; }

    public bool ShouldSendTrialConversionEmail { get; init; }
}
