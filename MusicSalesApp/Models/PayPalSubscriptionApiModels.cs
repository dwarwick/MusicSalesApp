using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

/// <summary>
/// A PayPal subscription plan hydrated from the plan details API.
/// Billing cycles, rather than the user-editable plan name, are authoritative for its terms.
/// </summary>
public sealed class PayPalPlan
{
    public string Id { get; init; }
    public string ProductId { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public string Status { get; init; }
    public decimal SetupFee { get; init; }
    public string SetupFeeCurrencyCode { get; init; }
    public decimal TaxPercentage { get; init; }
    public IReadOnlyList<PayPalBillingCycle> BillingCycles { get; init; } = Array.Empty<PayPalBillingCycle>();

    public PayPalBillingCycle RegularBillingCycle => BillingCycles
        .Where(cycle => string.Equals(cycle.TenureType, PayPalBillingTenureTypes.Regular, StringComparison.Ordinal))
        .OrderBy(cycle => cycle.Sequence)
        .FirstOrDefault();

    public IReadOnlyList<PayPalBillingCycle> TrialBillingCycles => BillingCycles
        .Where(cycle => string.Equals(cycle.TenureType, PayPalBillingTenureTypes.Trial, StringComparison.Ordinal))
        .OrderBy(cycle => cycle.Sequence)
        .ToArray();

    public IReadOnlyList<PayPalBillingCycle> FreeTrialBillingCycles => TrialBillingCycles
        .Where(cycle =>
            // PayPal permits a free trial to omit pricing_scheme entirely.
            (!cycle.FixedPrice.HasValue || cycle.FixedPrice == decimal.Zero)
            && cycle.TotalCycles > 0)
        .OrderBy(cycle => cycle.Sequence)
        .ToArray();

    public PayPalBillingCycle FreeTrialBillingCycle => FreeTrialBillingCycles.FirstOrDefault();

    public decimal RegularPrice => RegularBillingCycle?.FixedPrice ?? decimal.Zero;
    public string CurrencyCode => RegularBillingCycle?.CurrencyCode;
    public string IntervalUnit => RegularBillingCycle?.IntervalUnit;
    public int IntervalCount => RegularBillingCycle?.IntervalCount ?? 0;
    public bool HasTrial => TrialBillingCycles.Count > 0;
    public bool HasFreeTrial => FreeTrialBillingCycles.Count > 0;

    public int? TrialDays
    {
        get
        {
            var trials = FreeTrialBillingCycles;
            if (trials.Count == 0)
            {
                return null;
            }

            if (trials.Any(trial => !string.Equals(
                    trial.IntervalUnit,
                    PayPalBillingIntervals.Day,
                    StringComparison.Ordinal)))
            {
                return null;
            }

            return checked(trials.Sum(trial => trial.IntervalCount * trial.TotalCycles));
        }
    }
}

/// <summary>
/// A billing cycle defined on a PayPal plan.
/// </summary>
public sealed class PayPalBillingCycle
{
    public string TenureType { get; init; }
    public int Sequence { get; init; }
    public int TotalCycles { get; init; }
    public string IntervalUnit { get; init; }
    public int IntervalCount { get; init; }
    public decimal? FixedPrice { get; init; }
    public string CurrencyCode { get; init; }
}

/// <summary>
/// Execution state for one billing-cycle tenure on a PayPal subscription.
/// </summary>
public sealed class PayPalBillingCycleExecution
{
    public string TenureType { get; init; }
    public int Sequence { get; init; }
    public int CyclesCompleted { get; init; }
    public int CyclesRemaining { get; init; }
    public int TotalCycles { get; init; }
}

/// <summary>
/// Current subscription state returned by PayPal, including embedded plan terms.
/// </summary>
public sealed class PayPalSubscriptionDetails
{
    public string Id { get; init; }
    public string PlanId { get; init; }
    public string Status { get; init; }
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? NextBillingTime { get; init; }
    public DateTimeOffset? LastPaymentTime { get; init; }
    public int FailedPaymentsCount { get; init; }
    public bool HasBillingInfo { get; init; }
    public bool IsInTrial { get; init; }
    public DateTimeOffset? TrialEndTime { get; init; }
    public PayPalPlan Plan { get; init; }
    public IReadOnlyList<PayPalBillingCycleExecution> CycleExecutions { get; init; } = Array.Empty<PayPalBillingCycleExecution>();

    // Compatibility helpers for the application's DateTime-based subscription entity.
    public DateTime? StartDate => StartTime?.UtcDateTime;
    public DateTime? NextBillingDate => NextBillingTime?.UtcDateTime;
    public DateTime? LastPaymentDate => LastPaymentTime?.UtcDateTime;
    public DateTime? TrialEndDate => TrialEndTime?.UtcDateTime;
}

/// <summary>
/// Identifiers returned when a PayPal subscription is created.
/// </summary>
public sealed record PayPalCreatedSubscription(string Id, string ApprovalUrl);
