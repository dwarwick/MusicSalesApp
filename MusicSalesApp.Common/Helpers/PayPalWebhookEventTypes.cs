namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// PayPal webhook event names consumed by the subscription integration.
/// </summary>
public static class PayPalWebhookEventTypes
{
    public const string SubscriptionActivated = "BILLING.SUBSCRIPTION.ACTIVATED";
    public const string SubscriptionCancelled = "BILLING.SUBSCRIPTION.CANCELLED";
    public const string SubscriptionExpired = "BILLING.SUBSCRIPTION.EXPIRED";
    public const string SubscriptionSuspended = "BILLING.SUBSCRIPTION.SUSPENDED";
    public const string SubscriptionUpdated = "BILLING.SUBSCRIPTION.UPDATED";
    public const string SubscriptionPaymentFailed = "BILLING.SUBSCRIPTION.PAYMENT.FAILED";
    public const string PaymentSaleCompleted = "PAYMENT.SALE.COMPLETED";
    public const string CustomerDisputeCreated = "CUSTOMER.DISPUTE.CREATED";
}
