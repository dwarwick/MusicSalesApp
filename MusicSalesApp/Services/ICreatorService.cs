#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for managing creator accounts and their operations.
/// </summary>
public interface ICreatorService
{
    /// <summary>
    /// Gets a creator by their ID.
    /// </summary>
    Task<Creator?> GetCreatorByIdAsync(int creatorId);

    /// <summary>
    /// Gets a creator by their user ID.
    /// </summary>
    Task<Creator?> GetCreatorByUserIdAsync(int userId);

    /// <summary>
    /// Gets a creator by their PayPal Merchant ID.
    /// </summary>
    Task<Creator?> GetCreatorByMerchantIdAsync(string merchantId);

    /// <summary>
    /// Gets a creator by their PayPal tracking ID (used during onboarding).
    /// </summary>
    Task<Creator?> GetCreatorByTrackingIdAsync(string trackingId);

    /// <summary>
    /// Creates a new creator record for a user who wants to become a creator.
    /// </summary>
    Task<Creator> CreateCreatorAsync(int userId, string? displayName = null, string? bio = null);

    /// <summary>
    /// Updates the creator's PayPal onboarding information.
    /// </summary>
    Task<Creator> UpdateOnboardingInfoAsync(int creatorId, string trackingId, string referralUrl);

    /// <summary>
    /// Completes the creator's PayPal onboarding after they've finished the PayPal flow.
    /// </summary>
    Task<Creator> CompleteOnboardingAsync(int creatorId, string merchantId, bool paymentsReceivable, bool primaryEmailConfirmed);

    /// <summary>
    /// Updates the creator's onboarding status.
    /// </summary>
    Task<Creator> UpdateOnboardingStatusAsync(int creatorId, CreatorOnboardingStatus status);

    /// <summary>
    /// Checks if a user is an active creator who can upload and sell music.
    /// </summary>
    Task<bool> IsActiveCreatorAsync(int userId);

    /// <summary>
    /// Gets all active creators.
    /// </summary>
    Task<List<Creator>> GetActiveCreatorsAsync();

    /// <summary>
    /// Gets all creators (for admin management).
    /// </summary>
    Task<List<Creator>> GetAllCreatorsAsync();

    /// <summary>
    /// Updates a creator's profile information.
    /// </summary>
    Task<Creator> UpdateCreatorProfileAsync(int creatorId, string? displayName, string? bio);

    /// <summary>
    /// Activates a creator account (admin function).
    /// </summary>
    Task<Creator> ActivateCreatorAsync(int creatorId);

    /// <summary>
    /// Deactivates a creator account (admin function).
    /// </summary>
    Task<Creator> DeactivateCreatorAsync(int creatorId);

    /// <summary>
    /// Gets the creator ID for a given user ID, or null if the user is not a creator.
    /// </summary>
    Task<int?> GetCreatorIdForUserAsync(int userId);

    /// <summary>
    /// Stops a user from being a creator. This will:
    /// - Remove all their music from Azure storage
    /// - Mark all their songs as inactive in the database
    /// - Remove the Creator role from the user
    /// </summary>
    /// <param name="userId">The user ID of the creator</param>
    /// <returns>True if successful</returns>
    Task<bool> StopBeingCreatorAsync(int userId);

    /// <summary>
    /// Deletes a specific song by marking it inactive and removing from Azure storage.
    /// Only the creator who owns the song or an admin can delete it.
    /// </summary>
    /// <param name="songMetadataId">The ID of the song metadata</param>
    /// <param name="creatorId">The creator ID (for authorization)</param>
    /// <returns>True if successful</returns>
    Task<bool> DeleteCreatorSongAsync(int songMetadataId, int creatorId);

    /// <summary>
    /// Gets all songs owned by a creator.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <returns>List of song metadata for the creator</returns>
    Task<List<SongMetadata>> GetCreatorSongsAsync(int creatorId);

    /// <summary>
    /// Deactivates all songs for a creator (marks them inactive and removes from Azure storage).
    /// Used when a creator closes their account or stops being a creator.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <returns>Number of songs deactivated</returns>
    Task<int> DeactivateAllCreatorSongsAsync(int creatorId);

    /// <summary>
    /// Revokes a creator's consent to the platform. This is called when PayPal sends a
    /// MERCHANT.PARTNER-CONSENT.REVOKED webhook. This will:
    /// - Mark all their songs as inactive
    /// - Remove all their music from Azure storage
    /// - Mark the creator as inactive with ConsentRevoked status
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <returns>True if successful</returns>
    Task<bool> RevokeCreatorConsentAsync(int creatorId);
}
