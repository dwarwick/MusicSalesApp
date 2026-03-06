#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing tips from listeners to creators.
/// </summary>
public interface ITipService
{
    /// <summary>
    /// Validates whether a user can send a tip and returns an error message if not.
    /// </summary>
    Task<(bool CanTip, string? ErrorMessage)> ValidateTipAsync(int tipperUserId, int creatorId, decimal amount, string? ipAddress, string? fingerprint);

    /// <summary>
    /// Records a completed tip after PayPal payment.
    /// </summary>
    Task<Tip> RecordTipAsync(int tipperUserId, int creatorId, int? songMetadataId, decimal amount, string payPalOrderId, string? ipAddress, string? fingerprint);

    /// <summary>
    /// Gets tips for a creator grouped by status for the dashboard.
    /// </summary>
    Task<List<Tip>> GetTipsForCreatorAsync(int creatorId);

    /// <summary>
    /// Gets cleared tips that are eligible for payout (held for at least 7 full days).
    /// </summary>
    Task<List<Tip>> GetClearedTipsForPayoutAsync(int creatorId);

    /// <summary>
    /// Marks pending tips as cleared if their hold period has passed (7 full days).
    /// </summary>
    Task<int> ProcessPendingToClearedAsync();

    /// <summary>
    /// Marks cleared tips as paid after they have been included in a payout.
    /// </summary>
    Task MarkTipsAsPaidAsync(List<int> tipIds, string payPalPayoutTransactionId);
}
