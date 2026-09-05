#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Delivers a push message to devices on one platform.
/// </summary>
/// <remarks>
/// One implementation per transport, registered as a set and selected by
/// <see cref="Platform"/>. Android goes through Firebase Cloud Messaging and iOS goes straight to
/// APNs - deliberately not routing iOS through FCM as well, which would mean putting the Firebase
/// SDK into the iOS app head. That head already carries documented App Store launch-crash
/// workarounds around static registration and LLVM AOT, and a large native SDK is the kind of
/// change that reopens them. Direct APNs is a JWT and an HTTP/2 request with no SDK at all.
/// </remarks>
public interface IPushNotificationSender
{
    /// <summary>One of <c>PushPlatforms</c>.</summary>
    string Platform { get; }

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
