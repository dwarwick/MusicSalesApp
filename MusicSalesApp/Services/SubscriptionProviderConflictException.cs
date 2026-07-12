using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

public sealed class SubscriptionProviderConflictException : InvalidOperationException
{
    public SubscriptionProviderConflictException(string existingBillingSource, string requestedBillingSource)
        : base(
            $"A current {GetDisplayName(existingBillingSource)} subscription already exists. " +
            $"Cancel it and wait until its access period ends before starting a {GetDisplayName(requestedBillingSource)} subscription.")
    {
        ExistingBillingSource = existingBillingSource;
        RequestedBillingSource = requestedBillingSource;
    }

    public string ExistingBillingSource { get; }

    public string RequestedBillingSource { get; }

    private static string GetDisplayName(string billingSource)
    {
        return billingSource switch
        {
            BillingSources.GooglePlay => "Google Play",
            BillingSources.PayPal => "PayPal",
            BillingSources.Apple => "Apple App Store",
            _ => billingSource
        };
    }
}
