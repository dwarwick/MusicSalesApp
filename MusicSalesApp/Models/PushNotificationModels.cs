#nullable enable
namespace MusicSalesApp.Models;

/// <summary>
/// One notification to deliver, in platform-neutral terms.
/// </summary>
/// <param name="Title">Short heading, e.g. "New music from Alex Rivers".</param>
/// <param name="Body">One line of detail.</param>
/// <param name="Data">
/// Key/value payload the app reads to decide where a tap should land. Values are strings on both
/// platforms - FCM rejects non-string data values outright, and matching that here keeps one shape
/// for both transports.
/// </param>
public sealed record PushMessage(
    string Title,
    string Body,
    IReadOnlyDictionary<string, string>? Data = null);

/// <summary>
/// What happened when one message was sent to one device.
/// </summary>
public enum PushDeliveryOutcome
{
    /// <summary>The platform accepted it.</summary>
    Delivered,

    /// <summary>
    /// The platform rejected the TOKEN - unregistered, or not a valid token for this app. The row
    /// must be deactivated: retrying can never succeed, and a dead token retried forever is how a
    /// dispatcher ends up doing nothing but failing.
    /// </summary>
    TokenRejected,

    /// <summary>
    /// The platform rejected the MESSAGE, or refused for a reason that is not the token's fault
    /// (payload too large, a 4xx we did not anticipate). Not retryable, but the token survives.
    /// </summary>
    PermanentFailure,

    /// <summary>
    /// We could not ask - network, timeout, 5xx, throttling, or no credentials configured.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from the two refusals above, and the distinction is load-bearing:
    /// only this outcome leaves the notification unstamped so a later run retries it. Collapsing it
    /// into a failure would silently drop notifications whenever Firebase had a bad minute - the
    /// same reason the billing code separates "you own nothing" from "we could not reach the store".
    /// </remarks>
    TransportFailure,
}

/// <summary>
/// The result of sending to one device.
/// </summary>
public sealed record PushDeliveryResult(string Token, PushDeliveryOutcome Outcome, string? Detail = null)
{
    public bool Delivered => Outcome == PushDeliveryOutcome.Delivered;

    /// <summary>
    /// True when this attempt settled the matter, either way. False only for a transport failure,
    /// which is the one outcome worth trying again.
    /// </summary>
    public bool IsFinal => Outcome != PushDeliveryOutcome.TransportFailure;
}
