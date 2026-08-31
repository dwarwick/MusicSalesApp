#nullable enable

using Hangfire;

namespace MusicSalesApp.Services;

/// <summary>
/// Catch-up for PayPal lifecycle webhooks that never arrived.
///
/// <para>
/// A subscription cancelled at PayPal reaches this app only as a BILLING.SUBSCRIPTION.CANCELLED
/// webhook. If that delivery fails - a wrong PayPal:WebhookId, an unsubscribed event type, an
/// endpoint outage - the local row keeps saying ACTIVE and keeps granting access, and nothing
/// scheduled ever re-checks it. This sweep asks the provider directly.
/// </para>
/// </summary>
public interface IPayPalEntitlementDriftService
{
    /// <summary>
    /// Verifies every PayPal subscription that still grants access locally against the provider,
    /// applying exactly what the missed webhook would have applied. Returns how many rows changed.
    /// </summary>
    /// <remarks>
    /// The filter attribute belongs here, not on the implementation. Hangfire resolves filters from
    /// Job.Type/Job.Method, which for an interface-registered recurring job are this interface and
    /// this method - an attribute on the concrete class is never seen. Compare
    /// IImageVariantBackfillService, which gets this right.
    /// </remarks>
    /// <remarks>
    /// AutomaticRetry(0) is not optional alongside the lock. DisableConcurrentExecution throws
    /// DistributedLockTimeoutException rather than swallowing it, so without this the global
    /// default of ten retries turns one harmless overlap into eleven blocked workers and ten
    /// error transitions - which, now that Error reaches the admin mailbox, is ten emails about
    /// nothing. Every other locked job in this repo pairs the two attributes.
    /// </remarks>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    Task<int> ReconcileDriftedSubscriptionsAsync();
}
