namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for subscription status values.
/// Both writers and readers must reference these constants to prevent silent mismatches.
/// </summary>
public static class SubscriptionStatuses
{
    /// <summary>Subscription created, awaiting PayPal payment approval.</summary>
    public const string ApprovalPending = "APPROVAL_PENDING";

    /// <summary>Subscription is active and payments are being made.</summary>
    public const string Active = "ACTIVE";

    /// <summary>Subscription has been cancelled.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>Subscription has been suspended.</summary>
    public const string Suspended = "SUSPENDED";

    /// <summary>Subscription has expired.</summary>
    public const string Expired = "EXPIRED";
}
