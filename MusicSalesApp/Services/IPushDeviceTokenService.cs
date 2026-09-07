#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// The register / deactivate lifecycle of a device's push token.
/// </summary>
public interface IPushDeviceTokenService
{
    /// <summary>
    /// Records that this user can be reached on this device, or returns false when the platform or
    /// token is unusable.
    /// </summary>
    /// <remarks>
    /// Registering a token that already exists REASSIGNS it to the calling user rather than adding
    /// a row. Phones get handed on and accounts get signed out of, and a token still attached to
    /// the previous account would deliver one person's notifications to another - which is the one
    /// failure mode of this feature that is a privacy breach rather than an inconvenience.
    /// </remarks>
    Task<bool> RegisterAsync(
        int userId,
        string platform,
        string token,
        string? deviceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires one token, for a sign-out or an explicit opt-out on the device.
    /// </summary>
    Task<bool> UnregisterAsync(int userId, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// The live tokens for these users, grouped by user. Bulk because the dispatcher works through
    /// a batch of notifications at a time and must not issue a query per recipient.
    /// </summary>
    Task<IReadOnlyDictionary<int, List<PushDeviceToken>>> GetActiveTokensAsync(
        IEnumerable<int> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires tokens the platform has told us are dead.
    /// </summary>
    /// <remarks>
    /// The only reliable signal either service gives. Without acting on it the dispatcher spends
    /// every run re-sending to devices that were uninstalled months ago, and the failure rate
    /// climbs until real sends are lost in the noise.
    /// </remarks>
    Task<int> DeactivateAsync(
        IEnumerable<string> tokens,
        string reason,
        CancellationToken cancellationToken = default);
}
