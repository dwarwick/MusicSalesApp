using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Server-side client for PayPal subscription plans and subscriptions.
/// </summary>
public interface IPayPalSubscriptionApiService
{
    Task<IReadOnlyList<PayPalPlan>> GetActivePlansAsync(CancellationToken cancellationToken = default);
    Task<PayPalPlan> GetPlanAsync(string planId, CancellationToken cancellationToken = default);
    Task<PayPalSubscriptionDetails> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default);
    Task<PayPalCreatedSubscription> CreateSubscriptionAsync(
        string planId,
        string returnUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default);
    Task<bool> CancelSubscriptionAsync(
        string subscriptionId,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an unsuccessful or malformed response from the PayPal subscriptions API.
/// </summary>
public sealed class PayPalSubscriptionApiException : Exception
{
    public PayPalSubscriptionApiException(string message)
        : base(message)
    {
    }

    public PayPalSubscriptionApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
