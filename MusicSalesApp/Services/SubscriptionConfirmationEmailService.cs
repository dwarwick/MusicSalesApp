using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface ISubscriptionConfirmationEmailService
{
    Task<bool> SendConfirmationAsync(ApplicationUser user, Subscription subscription, string baseUrl);
    Task<bool> SendTrialStartedAsync(ApplicationUser user, Subscription subscription, string baseUrl);
    Task<bool> SendTrialConvertedAsync(ApplicationUser user, Subscription subscription, string baseUrl);
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
        => await SendSubscriptionEmailAsync(user, subscription, baseUrl, EmailKind.Confirmation);

    public async Task<bool> SendTrialStartedAsync(ApplicationUser user, Subscription subscription, string baseUrl)
        => await SendSubscriptionEmailAsync(user, subscription, baseUrl, EmailKind.TrialStarted);

    public async Task<bool> SendTrialConvertedAsync(ApplicationUser user, Subscription subscription, string baseUrl)
        => await SendSubscriptionEmailAsync(user, subscription, baseUrl, EmailKind.TrialConverted);

    private async Task<bool> SendSubscriptionEmailAsync(ApplicationUser user, Subscription subscription, string baseUrl, EmailKind emailKind)
    {
        if (subscription == null)
        {
            _logger.LogWarning("Skipping subscription {EmailKind} email because subscription details are unavailable.", emailKind);
            return false;
        }

        if (user == null || string.IsNullOrWhiteSpace(user.Email))
        {
            _logger.LogWarning("Skipping subscription {EmailKind} email because user email is unavailable.", emailKind);
            return false;
        }

        var externalSubscriptionId = ResolveExternalSubscriptionId(subscription);
        if (string.IsNullOrWhiteSpace(externalSubscriptionId))
        {
            _logger.LogWarning(
                "Skipping subscription {EmailKind} email for user {UserId} because no external subscription reference was found.",
                emailKind,
                user.Id);
            return false;
        }

        try
        {
            _logger.LogInformation(
                "Attempting subscription {EmailKind} email for user {UserId}. BillingSource={BillingSource}, SubscriptionId={SubscriptionId}, ExternalReference={ExternalReference}, MonthlyPrice={MonthlyPrice}, BaseUrl={BaseUrl}",
                emailKind,
                user.Id,
                subscription.BillingSource,
                subscription.Id,
                externalSubscriptionId,
                subscription.MonthlyPrice,
                baseUrl);

            var sent = emailKind switch
            {
                EmailKind.TrialStarted => await _purchaseEmailService.SendSubscriptionTrialStartedAsync(user.Email, user.UserName ?? user.Email, subscription, externalSubscriptionId, user.TimeZoneId, baseUrl),
                EmailKind.TrialConverted => await _purchaseEmailService.SendSubscriptionTrialConvertedAsync(user.Email, user.UserName ?? user.Email, subscription, externalSubscriptionId, user.TimeZoneId, baseUrl),
                _ => await _purchaseEmailService.SendSubscriptionConfirmationAsync(user.Email, user.UserName ?? user.Email, subscription, externalSubscriptionId, user.TimeZoneId, baseUrl)
            };

            if (sent)
            {
                _logger.LogInformation(
                    "Subscription {EmailKind} email sent for user {UserId}. BillingSource={BillingSource}, SubscriptionId={SubscriptionId}, ExternalReference={ExternalReference}",
                    emailKind,
                    user.Id,
                    subscription.BillingSource,
                    subscription.Id,
                    externalSubscriptionId);
            }
            else
            {
                _logger.LogWarning(
                    "Subscription {EmailKind} email service returned false for user {UserId}. BillingSource={BillingSource}, SubscriptionId={SubscriptionId}, ExternalReference={ExternalReference}",
                    emailKind,
                    user.Id,
                    subscription.BillingSource,
                    subscription.Id,
                    externalSubscriptionId);
            }

            return sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send subscription {EmailKind} email to user {UserId}", emailKind, user.Id);
            return false;
        }
    }

    private enum EmailKind
    {
        Confirmation,
        TrialStarted,
        TrialConverted
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