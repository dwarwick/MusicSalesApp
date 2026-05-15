using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Helpers;

public static class SubscriptionEntitlementHelper
{
    public static bool IsCurrentlyEntitled(Subscription subscription)
    {
        if (subscription == null)
        {
            return false;
        }

        var now = DateTime.UtcNow;

        if (string.Equals(subscription.Status, SubscriptionStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return !subscription.EndDate.HasValue || subscription.EndDate.Value > now;
        }

        if (string.Equals(subscription.Status, SubscriptionStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return subscription.EndDate.HasValue && subscription.EndDate.Value > now;
        }

        return false;
    }
}