#nullable enable

using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Why a reconcile attempt produced no reconciled subscription.
/// </summary>
public enum PayPalReconcileOutcome
{
    /// <summary>
    /// PayPal could not be asked, or answered something this app could not apply. The provider
    /// state is unknown, so no user-visible consequence may be drawn from it.
    ///
    /// <para>
    /// First deliberately, so it is the default value. This type crosses an interface, and a test
    /// double that returns default(PayPalReconcileAttempt) would otherwise hand callers
    /// "Reconciled" with a null Result - a NullReferenceException dressed as a success.
    /// </para>
    /// </summary>
    Unverifiable,

    /// <summary>The provider answered and the local row now matches it.</summary>
    Reconciled,

    /// <summary>
    /// PayPal returned 404: the agreement does not exist and cannot bill anyone. Definitive.
    /// </summary>
    ProviderConfirmedMissing
}

/// <summary>
/// The result of a reconcile attempt, keeping the two failure modes apart.
///
/// <para>
/// <see cref="IPayPalSubscriptionManagementService.ReconcileSubscriptionAsync(string, string, System.Threading.CancellationToken)"/>
/// flattens both failures to null, which is fine for callers that only need the reconciled row.
/// Callers that decide a user's access, or whether to alert, must not conflate "PayPal says this
/// agreement does not exist" with "PayPal did not answer" - the first is actionable and final, the
/// second is a transient outage.
/// </para>
/// </summary>
public readonly record struct PayPalReconcileAttempt(
    PayPalReconcileOutcome Outcome,
    PayPalSubscriptionReconciliationResult? Result)
{
    public static PayPalReconcileAttempt ProviderConfirmedMissing { get; }
        = new(PayPalReconcileOutcome.ProviderConfirmedMissing, null);

    public static PayPalReconcileAttempt Unverifiable { get; }
        = new(PayPalReconcileOutcome.Unverifiable, null);

    public static PayPalReconcileAttempt Reconciled(PayPalSubscriptionReconciliationResult result)
        => new(PayPalReconcileOutcome.Reconciled, result);
}
