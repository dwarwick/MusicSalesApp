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
    /// Creates a PayPal order for a tip and saves a pending tip record.
    /// Returns (success, errorMessage, approvalUrl) so the caller can redirect to PayPal.
    /// </summary>
    Task<(bool Success, string? ErrorMessage, string? ApprovalUrl)> CreateTipOrderAsync(
        int tipperUserId, int creatorId, int? songMetadataId,
        decimal amount, string? ipAddress, string? fingerprint, string returnUrl);

    /// <summary>
    /// Captures a PayPal order after buyer approval and updates the tip record.
    /// Called when the user returns from PayPal with the token (order ID).
    /// Returns (success, errorMessage).
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> CaptureTipAsync(string payPalOrderId);

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
