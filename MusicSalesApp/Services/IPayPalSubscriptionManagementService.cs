using MusicSalesApp.Models;

#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// Coordinates PayPal plan selection, checkout, provider reconciliation, and cancellation.
/// Both Blazor components and API controllers use this service so they apply identical rules.
/// </summary>
public interface IPayPalSubscriptionManagementService
{
    Task<PayPalWebOfferQuote> GetOfferQuoteAsync(int? userId, CancellationToken cancellationToken = default);

    Task<PayPalCheckoutResult> CreateSubscriptionAsync(
        ApplicationUser user,
        bool agreeToTerms,
        int? displayedOfferVersion,
        string? displayedPlanId,
        string fallbackBaseUrl,
        CancellationToken cancellationToken = default);

    Task<PayPalActivationResult> ActivateCurrentSubscriptionAsync(
        ApplicationUser user,
        string baseUrl,
        CancellationToken cancellationToken = default);

    Task<PayPalSubscriptionReconciliationResult?> ReconcileSubscriptionAsync(
        string paypalSubscriptionId,
        string baseUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles against the provider without emailing the subscriber about the transition.
    ///
    /// <para>
    /// For catch-up callers only. A sweep that settles a subscription cancelled months ago would
    /// otherwise send a "your subscription has been cancelled" notice long after the fact, which
    /// reads as a new event rather than bookkeeping. Anything reacting to a live provider
    /// notification should use the overload above and let the emails go out.
    /// </para>
    ///
    /// <para>
    /// An overload rather than an optional parameter: expression trees cannot omit optional
    /// arguments (CS0854), so adding one would break every existing mock of this method.
    /// </para>
    /// </summary>
    Task<PayPalSubscriptionReconciliationResult?> ReconcileSubscriptionAsync(
        string paypalSubscriptionId,
        string baseUrl,
        bool sendTransitionEmails,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles and reports *why* it could not be reconciled, which both overloads above flatten
    /// away to null.
    ///
    /// <para>
    /// Use this wherever the answer decides a user's access or whether to raise an alert. A
    /// provider-confirmed 404 is final - the agreement cannot bill anyone - while an unreachable
    /// PayPal means nothing at all, and treating the two alike either strands users behind a dead
    /// checkout or pages an admin nightly about a transient outage.
    /// </para>
    /// </summary>
    Task<PayPalReconcileAttempt> TryReconcileSubscriptionAsync(
        string paypalSubscriptionId,
        string baseUrl,
        bool sendTransitionEmails = true,
        CancellationToken cancellationToken = default);

    Task<PayPalCancellationResult> CancelSubscriptionAsync(
        ApplicationUser user,
        string baseUrl,
        CancellationToken cancellationToken = default);

    /// <param name="expectedPendingSubscriptionId">
    /// When set, the abandon is skipped unless the user's newest pending checkout is still this
    /// row. Background sweeps must pass it so a checkout started since the row was selected is
    /// never torn down.
    /// </param>
    Task<bool> AbandonPendingCheckoutAsync(
        ApplicationUser user,
        int? expectedPendingSubscriptionId = null,
        CancellationToken cancellationToken = default);

    Task<bool> RefreshIfNeededAsync(
        int userId,
        string baseUrl,
        CancellationToken cancellationToken = default);

    Task<PayPalMismatchResolutionResult> ResolveCurrentMismatchAsync(
        ApplicationUser user,
        string baseUrl,
        CancellationToken cancellationToken = default);

    Task<string?> GetOpenMismatchCorrelationIdAsync(
        int userId,
        CancellationToken cancellationToken = default);
}
