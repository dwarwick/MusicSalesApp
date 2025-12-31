#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for managing seller accounts and their operations.
/// </summary>
public interface ISellerService
{
    /// <summary>
    /// Gets a seller by their user ID.
    /// </summary>
    Task<Seller?> GetSellerByUserIdAsync(int userId);

    /// <summary>
    /// Gets a seller by their PayPal Merchant ID.
    /// </summary>
    Task<Seller?> GetSellerByMerchantIdAsync(string merchantId);

    /// <summary>
    /// Gets a seller by their PayPal tracking ID (used during onboarding).
    /// </summary>
    Task<Seller?> GetSellerByTrackingIdAsync(string trackingId);

    /// <summary>
    /// Creates a new seller record for a user who wants to become a seller.
    /// </summary>
    Task<Seller> CreateSellerAsync(int userId, string? displayName = null, string? bio = null);

    /// <summary>
    /// Updates the seller's PayPal onboarding information.
    /// </summary>
    Task<Seller> UpdateOnboardingInfoAsync(int sellerId, string trackingId, string referralUrl);

    /// <summary>
    /// Completes the seller's PayPal onboarding after they've finished the PayPal flow.
    /// </summary>
    Task<Seller> CompleteOnboardingAsync(int sellerId, string merchantId, bool paymentsReceivable, bool primaryEmailConfirmed);

    /// <summary>
    /// Updates the seller's onboarding status.
    /// </summary>
    Task<Seller> UpdateOnboardingStatusAsync(int sellerId, SellerOnboardingStatus status);

    /// <summary>
    /// Checks if a user is an active seller who can upload and sell music.
    /// </summary>
    Task<bool> IsActiveSellerAsync(int userId);

    /// <summary>
    /// Gets all active sellers.
    /// </summary>
    Task<List<Seller>> GetActiveSellersAsync();

    /// <summary>
    /// Gets all sellers (for admin management).
    /// </summary>
    Task<List<Seller>> GetAllSellersAsync();

    /// <summary>
    /// Updates a seller's profile information.
    /// </summary>
    Task<Seller> UpdateSellerProfileAsync(int sellerId, string? displayName, string? bio);

    /// <summary>
    /// Activates a seller account (admin function).
    /// </summary>
    Task<Seller> ActivateSellerAsync(int sellerId);

    /// <summary>
    /// Deactivates a seller account (admin function).
    /// </summary>
    Task<Seller> DeactivateSellerAsync(int sellerId);

    /// <summary>
    /// Gets the seller ID for a given user ID, or null if the user is not a seller.
    /// </summary>
    Task<int?> GetSellerIdForUserAsync(int userId);

    /// <summary>
    /// Stops a user from being a seller. This will:
    /// - Remove all their music from Azure storage
    /// - Mark all their songs as inactive in the database
    /// - Remove the Seller role from the user
    /// </summary>
    /// <param name="userId">The user ID of the seller</param>
    /// <returns>True if successful</returns>
    Task<bool> StopBeingSellerAsync(int userId);

    /// <summary>
    /// Deletes a specific song by marking it inactive and removing from Azure storage.
    /// Only the seller who owns the song or an admin can delete it.
    /// </summary>
    /// <param name="songMetadataId">The ID of the song metadata</param>
    /// <param name="sellerId">The seller ID (for authorization)</param>
    /// <returns>True if successful</returns>
    Task<bool> DeleteSellerSongAsync(int songMetadataId, int sellerId);

    /// <summary>
    /// Gets all songs owned by a seller.
    /// </summary>
    /// <param name="sellerId">The seller ID</param>
    /// <returns>List of song metadata for the seller</returns>
    Task<List<SongMetadata>> GetSellerSongsAsync(int sellerId);

    /// <summary>
    /// Deactivates all songs for a seller (marks them inactive and removes from Azure storage).
    /// Used when a seller closes their account or stops being a seller.
    /// </summary>
    /// <param name="sellerId">The seller ID</param>
    /// <returns>Number of songs deactivated</returns>
    Task<int> DeactivateAllSellerSongsAsync(int sellerId);
}
