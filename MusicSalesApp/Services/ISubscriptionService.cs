using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface ISubscriptionService
{
    Task<Subscription> GetActiveSubscriptionAsync(int userId);
    Task<bool> HasActiveSubscriptionAsync(int userId);
    Task<Subscription> CreateSubscriptionAsync(int userId, string paypalSubscriptionId, decimal monthlyPrice);
    Task<bool> CancelSubscriptionAsync(int userId);
    Task<Subscription> GetSubscriptionByPayPalIdAsync(string paypalSubscriptionId);
    Task UpdateSubscriptionStatusAsync(string paypalSubscriptionId, string status, DateTime? nextBillingDate = null);
    Task UpdateSubscriptionDetailsAsync(string paypalSubscriptionId, DateTime? nextBillingDate, DateTime? lastPaymentDate);
    Task<bool> DeletePendingSubscriptionAsync(int userId);
    Task<Subscription> GetPendingSubscriptionAsync(int userId);
    Task ActivateSubscriptionAsync(string paypalSubscriptionId, DateTime? nextBillingDate, DateTime? lastPaymentDate);

    // Google Play billing methods
    Task<Subscription> CreateGooglePlaySubscriptionAsync(int userId, string purchaseToken, string orderId, decimal monthlyPrice);
    Task<Subscription> GetSubscriptionByGooglePlayTokenAsync(string purchaseToken);
    Task UpdateGooglePlaySubscriptionStatusAsync(string purchaseToken, string status, DateTime? expiryTime = null);
}
