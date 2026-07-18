using MusicSalesApp.Models;

#nullable enable

namespace MusicSalesApp.Services;

public interface IPayPalSubscriptionAnomalyService
{
    Task<PayPalSubscriptionAnomaly?> GetOpenEpisodeAsync(
        int subscriptionId,
        CancellationToken cancellationToken = default);

    Task<PayPalSubscriptionAnomaly> RecordMismatchAsync(
        Subscription subscription,
        ApplicationUser user,
        PayPalSubscriptionDetails providerDetails,
        string? reconciliationError,
        string baseUrl,
        CancellationToken cancellationToken = default);

    Task ResolveOpenEpisodeAsync(
        int subscriptionId,
        CancellationToken cancellationToken = default);
}
