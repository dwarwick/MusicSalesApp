namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Shared PayPal subscription values used across plan selection and checkout.
/// </summary>
public static class PayPalSubscriptionDefaults
{
    public const string UsdCurrencyCode = "USD";
    public const string UserCancellationReason = "User requested cancellation";
    public const int RefreshBoundaryMinutes = 5;
    public const int AnomalyNotificationClaimMinutes = 15;

    /// <summary>
    /// How long an entitlement mismatch must persist before the user and admin are emailed.
    /// Suppresses transient mismatches that self-heal once a PayPal payment webhook lands.
    /// </summary>
    public const int AnomalyNotificationGraceMinutes = 30;

    /// <summary>
    /// How old an APPROVAL_PENDING checkout must be before the nightly sweep treats it as
    /// abandoned. Deliberately generous: same-user re-subscribes are already cleared in the
    /// checkout flow, so this only needs to catch rows nobody will ever return to.
    /// </summary>
    public const int StalePendingCheckoutHours = 72;

    /// <summary>
    /// Maximum stale checkouts processed per sweep. Each one costs at least one PayPal API call.
    /// </summary>
    public const int StalePendingCheckoutBatchSize = 200;
}
