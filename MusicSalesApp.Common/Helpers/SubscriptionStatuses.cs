namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for subscription status values.
/// Both writers and readers must reference these constants to prevent silent mismatches.
/// </summary>
public static class SubscriptionStatuses
{
    /// <summary>Subscription created, awaiting PayPal payment approval.</summary>
    public const string ApprovalPending = "APPROVAL_PENDING";

    /// <summary>PayPal buyer approval completed while provider activation is still pending.</summary>
    public const string Approved = "APPROVED";

    /// <summary>Subscription is active and payments are being made.</summary>
    public const string Active = "ACTIVE";

    /// <summary>Subscription has been cancelled.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>Alternative spelling used by some external providers for cancelled subscriptions.</summary>
    public const string Canceled = "CANCELED";

    /// <summary>Subscription has been suspended.</summary>
    public const string Suspended = "SUSPENDED";

    /// <summary>Subscription has expired.</summary>
    public const string Expired = "EXPIRED";
}
