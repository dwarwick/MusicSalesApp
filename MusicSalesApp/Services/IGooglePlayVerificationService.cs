namespace MusicSalesApp.Services;

/// <summary>
/// Verifies Google Play subscription purchases using the Google Play Developer API.
/// </summary>
public interface IGooglePlayVerificationService
{
    /// <summary>
    /// Verifies a subscription purchase token with Google Play and returns the subscription details.
    /// </summary>
    Task<GooglePlaySubscriptionInfo> VerifySubscriptionAsync(string purchaseToken, string productId);

    /// <summary>
    /// Acknowledges a subscription purchase so it is not automatically refunded after 3 days.
    /// </summary>
    Task<bool> AcknowledgeSubscriptionAsync(string purchaseToken, string productId);

    /// <summary>
    /// Cancels a Google Play subscription. The subscription remains valid until its expiry time.
    /// </summary>
    Task<bool> CancelSubscriptionAsync(string purchaseToken, string productId);
}

/// <summary>
/// Subscription details returned from Google Play Developer API verification.
/// </summary>
public record GooglePlaySubscriptionInfo(
    string SubscriptionState,
    DateTimeOffset? ExpiryTime,
    string OrderId,
    bool IsAcknowledged,
    string LinkedPurchaseToken);
