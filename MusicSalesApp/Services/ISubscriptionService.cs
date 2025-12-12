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
}
