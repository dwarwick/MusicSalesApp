namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// PayPal billing interval values used by subscription plan billing cycles.
/// </summary>
public static class PayPalBillingIntervals
{
    public const string Day = "DAY";
    public const string Month = "MONTH";
}

/// <summary>
/// PayPal billing-cycle tenure values.
/// </summary>
public static class PayPalBillingTenureTypes
{
    public const string Trial = "TRIAL";
    public const string Regular = "REGULAR";
}

/// <summary>
/// PayPal billing-plan status values.
/// </summary>
public static class PayPalPlanStatuses
{
    public const string Active = "ACTIVE";
}

/// <summary>
/// Link relation values returned by the PayPal subscriptions API.
/// </summary>
public static class PayPalLinkRelations
{
    public const string Approve = "approve";
    public const string Next = "next";
}

/// <summary>
/// PayPal API error names used to implement idempotent operations.
/// </summary>
public static class PayPalApiErrorNames
{
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
}
