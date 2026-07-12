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

    Task<PayPalCancellationResult> CancelSubscriptionAsync(
        ApplicationUser user,
        string baseUrl,
        CancellationToken cancellationToken = default);

    Task<bool> AbandonPendingCheckoutAsync(
        ApplicationUser user,
        CancellationToken cancellationToken = default);

    Task<bool> RefreshIfNeededAsync(
        int userId,
        string baseUrl,
        CancellationToken cancellationToken = default);
}
