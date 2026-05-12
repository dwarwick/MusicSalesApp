using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface ISubscriptionConfirmationEmailService
{
    Task<bool> SendConfirmationAsync(ApplicationUser user, Subscription subscription, string baseUrl);
}

public class SubscriptionConfirmationEmailService : ISubscriptionConfirmationEmailService
{
    private readonly IPurchaseEmailService _purchaseEmailService;
    private readonly ILogger<SubscriptionConfirmationEmailService> _logger;

    public SubscriptionConfirmationEmailService(
        IPurchaseEmailService purchaseEmailService,
        ILogger<SubscriptionConfirmationEmailService> logger)
    {
        _purchaseEmailService = purchaseEmailService;
        _logger = logger;
    }

    public async Task<bool> SendConfirmationAsync(ApplicationUser user, Subscription subscription, string baseUrl)
    {
        if (user == null || string.IsNullOrWhiteSpace(user.Email))
        {
            _logger.LogWarning("Skipping subscription confirmation email because user email is unavailable.");
            return false;
        }

        var externalSubscriptionId = ResolveExternalSubscriptionId(subscription);
        if (string.IsNullOrWhiteSpace(externalSubscriptionId))
        {
            _logger.LogWarning(
                "Skipping subscription confirmation email for user {UserId} because no external subscription reference was found.",
                user.Id);
            return false;
        }

        try
        {
            return await _purchaseEmailService.SendSubscriptionConfirmationAsync(
                user.Email,
                user.UserName ?? user.Email,
                subscription,
                externalSubscriptionId,
                baseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send subscription confirmation email to user {UserId}", user.Id);
            return false;
        }
    }

    private static string ResolveExternalSubscriptionId(Subscription subscription)
    {
        return subscription.BillingSource switch
        {
            BillingSources.PayPal => subscription.PayPalSubscriptionId,
            BillingSources.GooglePlay => subscription.GooglePlayOrderId ?? subscription.GooglePlayPurchaseToken,
            BillingSources.Apple => subscription.AppStoreOriginalTransactionId ?? subscription.AppStoreTransactionId,
            _ => subscription.PayPalSubscriptionId
                ?? subscription.GooglePlayOrderId
                ?? subscription.GooglePlayPurchaseToken
                ?? subscription.AppStoreOriginalTransactionId
                ?? subscription.AppStoreTransactionId
        };
    }
}