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
    /// <param name="errorMessage">Optional error message to store (only kept when status is Pending; cleared otherwise)</param>
    /// <returns>The updated creator</returns>
    Task<Creator> UpdateTaxFormStatusAsync(int creatorId, TaxFormStatus status, string? errorMessage = null);

    /// <summary>
    /// Records the timestamp of a failed Instant TIN Match for 24-hour cooldown enforcement.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <returns>The updated creator</returns>
    Task<Creator> SetTinMatchFailedAsync(int creatorId);

    /// <summary>
    /// Updates the creator's tax form status along with full tax residency data.
    /// Called when W-9 or W-8BEN form is completed via TaxBandits webhook.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <param name="status">The new tax form status</param>
    /// <param name="taxResidencyType">US for W-9 filers, Foreign for W-8BEN filers</param>
    /// <param name="taxResidencyCountry">ISO-2 country code of tax residency</param>
    /// <param name="withholdingRate">The withholding rate as decimal (e.g., 0.24 for 24%)</param>
    /// <param name="taxFormExpirationDate">When the tax form expires (W-8 only)</param>
    /// <param name="taxBanditsSubmissionId">The TaxBandits submission ID for reference</param>
    /// <param name="subjectToBackupWithholding">Whether US creator is subject to backup withholding (W-9 only)</param>
    /// <returns>The updated creator</returns>
    Task<Creator> UpdateTaxFormStatusWithTaxDataAsync(
        int creatorId,
        TaxFormStatus status,
        TaxResidencyType taxResidencyType,
        string? taxResidencyCountry,
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
    /// Updates the active creator's payout email address and PayPal affirmation.
    /// </summary>
    Task<Creator?> UpdateCreatorPayoutEmailAsync(int userId, string? payoutEmail, bool payPalAccountAffirmed);

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
    /// Sets OnboardingStatus to Completed and preserves existing payout setup unless optional
    /// PayPal payout details are supplied.
    /// This is used instead of direct DbContext manipulation in the controller
    /// to ensure an atomic, testable state transition.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <param name="payPalEmail">The creator's owned or authorized PayPal email for payouts</param>
    /// <param name="payPalAccountAffirmed">Whether the creator affirmed they own or are authorized to use the PayPal account</param>
    /// <returns>The updated creator</returns>
    Task<Creator> ResetCreatorOnboardingAsync(int creatorId, string? payPalEmail, bool payPalAccountAffirmed);

    /// <summary>
    /// Updates the creator's location certification and acknowledgment.
    /// Stores the certification selection, acknowledgment acceptance, and the UTC timestamp.
    /// </summary>
    /// <param name="creatorId">The creator ID</param>
    /// <param name="locationCertification">The location certification selection</param>
    /// <param name="acknowledgmentAccepted">Whether the acknowledgment was accepted</param>
    /// <returns>The updated creator</returns>
    Task<Creator> UpdateLocationCertificationAsync(int creatorId, CreatorLocationCertification locationCertification, bool acknowledgmentAccepted);

    /// <summary>
    /// Records the creator's assertion that they understand PayPal and tax form completion
    /// are required before receiving payouts.
    /// </summary>
    Task<Creator> UpdatePayoutRequirementsAcknowledgmentAsync(int creatorId, bool payoutRequirementsAcknowledged);

    /// <summary>
    /// Orchestrates creator activation: creates or updates the creator record, stores Creator
    /// Agreement acceptance, activates creator tools, and preserves payout setup for later.
    /// </summary>
    Task<StartOnboardingResult> StartOnboardingAsync(CreatorOnboardingInput request);

    /// <summary>
    /// Checks the creator's signup certification status, activates eligible creators,
    /// and assigns the Creator role when needed.
    /// </summary>
    Task<CompleteOnboardingResult> CompleteOnboardingAsync(int userId);

    /// <summary>
    /// Initiates a tax form update for an active creator who wants to submit a new W-8/W-9.
    /// Sets the tax form status to Pending and updates the PayeeRef.
    /// </summary>
    Task<InitiateTaxFormUpdateResult> InitiateTaxFormUpdateAsync(int userId, string? email);
}

/// <summary>
/// Request data for starting creator onboarding.
/// </summary>
public class CreatorOnboardingInput
{
    public int UserId { get; set; }
    public string? UserEmail { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? PayPalEmail { get; set; }
    public bool PayPalAccountAffirmed { get; set; }
    public bool CreatorAgreementAccepted { get; set; }
    public CreatorLocationCertification LocationCertification { get; set; }
    public bool AcknowledgmentAccepted { get; set; }
    public bool PayoutRequirementsAcknowledged { get; set; }
    public bool SubmitTaxFormNow { get; set; }
}

/// <summary>
/// Result of the StartOnboarding operation.
/// </summary>
public class StartOnboardingResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>True if the creator is active after signup.</summary>
    public bool IsActive { get; set; }

    /// <summary>True if the creator chose to fill out a tax form immediately after signup.</summary>
    public bool TaxFormPending { get; set; }

    /// <summary>True if the creator is ineligible (e.g. non-US person inside US).</summary>
    public bool IsIneligible { get; set; }

    public static StartOnboardingResult Failure(string errorMessage) =>
        new() { Success = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of the CompleteOnboarding operation.
/// </summary>
public class CompleteOnboardingResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsActive { get; set; }
    public bool PaymentsReceivable { get; set; }
    public bool PrimaryEmailConfirmed { get; set; }

    public static CompleteOnboardingResult Failure(string errorMessage) =>
        new() { Success = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of the InitiateTaxFormUpdate operation.
/// </summary>
public class InitiateTaxFormUpdateResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public static InitiateTaxFormUpdateResult Failure(string errorMessage) =>
        new() { Success = false, ErrorMessage = errorMessage };
}
