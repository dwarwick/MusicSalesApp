namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Shared PayPal subscription values used across plan selection and checkout.
/// </summary>
public static class PayPalSubscriptionDefaults
{
    public const string LegacyPlanNamePrefix = "Music Streaming Monthly Subscription - $";
    public const string UsdCurrencyCode = "USD";
    public const string UserCancellationReason = "User requested cancellation";
    public const int RefreshBoundaryMinutes = 5;
}
