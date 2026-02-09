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
    /// Creates a new creator record for a user who wants to become a creator.
    /// </summary>
    Task<Creator> CreateCreatorAsync(int userId, string? displayName = null, string? bio = null);

    /// <summary>
    /// Updates the creator's onboarding status.
    /// </summary>
    Task<Creator> UpdateOnboardingStatusAsync(int creatorId, CreatorOnboardingStatus status);

    /// <summary>
    /// Updates the creator's tax form (W-9/W-8) status.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <param name="status">The new tax form status</param>
    /// <returns>The updated creator</returns>
    Task<Creator> UpdateTaxFormStatusAsync(int creatorId, TaxFormStatus status);

    /// <summary>
    /// Updates the creator's tax form status along with full tax residency data.
    /// Called when W-9 or W-8BEN form is completed via TaxBandits webhook.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <param name="status">The new tax form status</param>
    /// <param name="taxResidencyType">US for W-9 filers, Foreign for W-8BEN filers</param>
    /// <param name="taxResidencyCountry">ISO-2 country code of tax residency</param>
    /// <param name="treatyCountry">ISO-2 code of treaty country if claiming treaty benefits</param>
    /// <param name="claimedTreatyArticle">The treaty article being claimed</param>
    /// <param name="withholdingRate">The withholding rate as decimal (e.g., 0.30 for 30%)</param>
    /// <param name="taxFormExpirationDate">When the tax form expires (W-8 only)</param>
    /// <param name="taxBanditsSubmissionId">The TaxBandits submission ID for reference</param>
    /// <param name="subjectToBackupWithholding">Whether US creator is subject to backup withholding (W-9 only)</param>
    /// <returns>The updated creator</returns>
    Task<Creator> UpdateTaxFormStatusWithTaxDataAsync(
        int creatorId,
        TaxFormStatus status,
        TaxResidencyType taxResidencyType,
        string? taxResidencyCountry,
        string? treatyCountry,
        string? claimedTreatyArticle,
        decimal withholdingRate,
        DateTime? taxFormExpirationDate,
        Guid? taxBanditsSubmissionId,
        bool subjectToBackupWithholding = false);

    /// <summary>
    /// Updates the creator's TaxBandits PayeeRef (typically the email used for W-9/W-8 requests).
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <param name="payeeRef">The PayeeRef value (typically the user's email)</param>
    /// <returns>The updated creator</returns>
    Task<Creator> UpdateTaxBanditsPayeeRefAsync(int creatorId, string payeeRef);

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
    /// Deactivates all songs for a creator and cleans up related data.
    /// Used when a creator closes their account or stops being a creator.
    /// Removes blobs from Azure storage, marks SongMetadata as inactive (retained for financial/tax records),
    /// and deletes UserPlaylist, SongLike, and RecommendedPlaylist entries.
    /// StreamPayout and SongStatusHistory records are preserved.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <returns>Number of songs deactivated</returns>
    Task<int> DeactivateAllCreatorSongsAsync(int creatorId);

    /// <summary>
    /// Revokes a creator's consent to the platform. This will:
    /// - Mark all their songs as inactive
    /// - Remove all their music from Azure storage
    /// - Mark the creator as inactive with ConsentRevoked status
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <returns>True if successful</returns>
    Task<bool> RevokeCreatorConsentAsync(int creatorId);

    /// <summary>
    /// Resets a returning creator's onboarding fields when they re-sign up.
    /// Sets OnboardingStatus to Completed, stores PayPal email and affirmation,
    /// and marks PaymentsReceivable/PrimaryEmailConfirmed as true.
    /// This is used instead of direct DbContext manipulation in the controller
    /// to ensure an atomic, testable state transition.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <param name="payPalEmail">The creator's PayPal email for payouts</param>
    /// <param name="payPalAccountAffirmed">Whether the creator affirmed their PayPal account</param>
    /// <returns>The updated creator</returns>
    Task<Creator> ResetCreatorOnboardingAsync(int creatorId, string payPalEmail, bool payPalAccountAffirmed);

    /// <summary>
    /// Updates the creator's location certification and acknowledgment.
    /// Stores the certification selection, acknowledgment acceptance, and the UTC timestamp.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <param name="locationCertification">The location certification selection</param>
    /// <param name="acknowledgmentAccepted">Whether the acknowledgment was accepted</param>
    /// <returns>The updated creator</returns>
    Task<Creator> UpdateLocationCertificationAsync(int creatorId, CreatorLocationCertification locationCertification, bool acknowledgmentAccepted);
}
