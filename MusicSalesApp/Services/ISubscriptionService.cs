using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface ISubscriptionService
{
    Task<Subscription> GetActiveSubscriptionAsync(int userId);
    Task<Subscription> GetLatestSubscriptionAsync(int userId);
    Task<bool> HasActiveSubscriptionAsync(int userId);
    Task<int> NormalizeExpiredSubscriptionsAsync();
    Task<Subscription> CreateSubscriptionAsync(int userId, string paypalSubscriptionId, decimal monthlyPrice);
    Task<bool> CancelSubscriptionAsync(int userId);
    Task<Subscription> GetSubscriptionByPayPalIdAsync(string paypalSubscriptionId);
    Task UpdateSubscriptionStatusAsync(string paypalSubscriptionId, string status, DateTime? nextBillingDate = null);
    Task UpdateSubscriptionDetailsAsync(string paypalSubscriptionId, DateTime? nextBillingDate, DateTime? lastPaymentDate);
    Task<bool> DeletePendingSubscriptionAsync(int userId);
    Task<Subscription> GetPendingSubscriptionAsync(int userId);
    Task ActivateSubscriptionAsync(string paypalSubscriptionId, DateTime? nextBillingDate, DateTime? lastPaymentDate);

    // Google Play billing methods
    Task<Subscription> CreateGooglePlaySubscriptionAsync(int userId, string purchaseToken, string orderId, decimal monthlyPrice, GooglePlaySubscriptionInfo googlePlayInfo = null);
    Task<Subscription> GetSubscriptionByGooglePlayTokenAsync(string purchaseToken);
    Task UpdateGooglePlaySubscriptionStatusAsync(string purchaseToken, string status, DateTime? expiryTime = null, GooglePlaySubscriptionInfo googlePlayInfo = null);
    Task UpdateGooglePlayStorePriceAsync(string purchaseToken, string formattedPrice, string priceCurrencyCode);
    Task MarkTrialActivationEmailSentAsync(int subscriptionId);
    Task MarkTrialConversionEmailSentAsync(int subscriptionId);

    // Apple App Store billing methods
    Task<Subscription> CreateAppleSubscriptionAsync(
        int userId,
        string transactionId,
        string originalTransactionId,
        string productId,
        string appAccountToken,
        string environment,
        decimal monthlyPrice,
        DateTime? startDate = null);
    Task<Subscription> GetSubscriptionByAppleTransactionIdAsync(string transactionId);
    Task<Subscription> GetSubscriptionByAppleOriginalTransactionIdAsync(string originalTransactionId);
    Task UpdateAppleSubscriptionStatusAsync(
        string originalTransactionId,
        string status,
        DateTime? expiryTime = null,
        string latestTransactionId = null,
        string environment = null,
        string productId = null,
        string appAccountToken = null,
        decimal? monthlyPrice = null);
}
