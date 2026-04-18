namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for subscription billing source values.
/// Both writers and readers must reference these constants to prevent silent mismatches.
/// </summary>
public static class BillingSources
{
    /// <summary>Subscription created via PayPal billing.</summary>
    public const string PayPal = "PayPal";

    /// <summary>Subscription created via Google Play in-app billing.</summary>
    public const string GooglePlay = "GooglePlay";

    /// <summary>Subscription created via Apple App Store in-app purchase.</summary>
    public const string Apple = "Apple";
}
