#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Delivers a push message to devices.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transport serves both platforms:</b> Firebase Cloud Messaging relays to APNs for iOS, so
/// the server never speaks to Apple directly. The APNs auth key is uploaded to the Firebase console
/// rather than living on this server.
/// </para>
/// <para>
/// That is what removes the sandbox/production token split, the nastiest failure mode in push: FCM
/// records the APNs environment against each token at registration, so a development-signed build
/// and a TestFlight build can both be delivered to without the server knowing - or needing to know
/// - which is which. Talking to APNs directly means owning that distinction forever, and getting it
/// wrong produces an unhelpful BadDeviceToken in both directions.
/// </para>
/// <para>
/// This stays an interface rather than collapsing into the dispatcher so a second transport can be
/// added later without the dispatcher learning about transports.
/// </para>
/// </remarks>
public interface IPushNotificationSender
{
    /// <summary>
    /// False when the credentials are absent. The whole feature is inert rather than broken in that
    /// state, matching how Sign in with Apple revocation behaves without its key.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends one message to each token. Never throws for a delivery problem - every token comes
    /// back with an outcome, because the caller has to tell "deactivate this token" from "try again
    /// later" per device, not per batch.
    /// </summary>
    Task<IReadOnlyList<PushDeliveryResult>> SendAsync(
        PushMessage message,
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken = default);
}
