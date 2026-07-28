#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// Background maintenance for PayPal checkouts and mismatch episodes. Keeps abandoned
/// APPROVAL_PENDING rows from accumulating, and guarantees a persistent mismatch is eventually
/// reported even if the affected user never returns to the site.
/// </summary>
public interface IPayPalCheckoutHygieneService
{
    /// <summary>
    /// Verifies APPROVAL_PENDING PayPal checkouts older than
    /// <see cref="MusicSalesApp.Common.Helpers.PayPalSubscriptionDefaults.StalePendingCheckoutHours"/>
    /// against PayPal and closes out the ones the buyer never approved.
    /// </summary>
    /// <returns>The number of stale checkouts successfully closed.</returns>
    Task<int> CleanupStalePendingCheckoutsAsync();

    /// <summary>
    /// Re-reconciles open mismatch episodes whose notifications are still withheld by the grace
    /// window, so they either self-resolve or release their emails.
    /// </summary>
    /// <returns>The number of episodes re-observed.</returns>
    Task<int> ReobserveOpenMismatchEpisodesAsync();
}
