#nullable enable
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing creator accounts and their operations.
/// </summary>
public class CreatorService : ICreatorService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IAzureStorageService _storageService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CreatorService> _logger;
    private readonly IAppSettingsService _appSettingsService;
    private readonly IAdminNotificationService _adminNotificationService;
    private readonly ICreatorPersonaService _creatorPersonaService;
    private readonly ICreatorEmailService _creatorEmailService;

    public CreatorService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IAzureStorageService storageService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<CreatorService> logger,
        IAppSettingsService appSettingsService,
        IAdminNotificationService adminNotificationService,
        ICreatorPersonaService creatorPersonaService,
        ICreatorEmailService creatorEmailService)
    {
        _dbContextFactory = dbContextFactory;
        _storageService = storageService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
        _appSettingsService = appSettingsService;
        _adminNotificationService = adminNotificationService;
        _creatorPersonaService = creatorPersonaService;
        _creatorEmailService = creatorEmailService;
    }

    /// <inheritdoc />
    public async Task<Creator?> GetCreatorByIdAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Creators
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Id == creatorId);
    }

    /// <inheritdoc />
    public async Task<Creator?> GetCreatorByUserIdAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Creators
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.UserId == userId);
    }

    /// <inheritdoc />
    public async Task<Creator> CreateCreatorAsync(int userId, string? displayName = null, string? bio = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Check if creator already exists
        var existingCreator = await context.Creators.FirstOrDefaultAsync(s => s.UserId == userId);
        if (existingCreator != null)
        {
            _logger.LogWarning("Attempt to create duplicate creator record for user {UserId}", userId);
            return existingCreator;
        }

        // Get the stream pay rate from settings (default $5 per 1000 streams)
        var streamPayRate = await _appSettingsService.GetStreamPayRateAsync();

        // Get the stream qualifying seconds from settings (default 30 seconds)
        var streamQualifyingSeconds = await _appSettingsService.GetStreamQualifyingSecondsAsync();

        var creator = new Creator
        {
            UserId = userId,
            DisplayName = displayName,
            Bio = bio,
            StreamPayRate = streamPayRate, // Set from app settings
            StreamQualifyingSeconds = streamQualifyingSeconds, // Set from app settings
            OnboardingStatus = CreatorOnboardingStatus.NotStarted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created creator record for user {UserId} with stream pay rate ${StreamRate:F6} per stream, qualifying seconds: {Seconds}", 
            userId, streamPayRate, streamQualifyingSeconds);
        return creator;
    }

    /// <inheritdoc />
    public async Task<Creator> UpdateOnboardingStatusAsync(int creatorId, CreatorOnboardingStatus status)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.OnboardingStatus = status;
        creator.UpdatedAt = DateTime.UtcNow;

        if (status == CreatorOnboardingStatus.Failed || status == CreatorOnboardingStatus.Suspended)
        {
            creator.IsActive = false;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated onboarding status for creator {CreatorId} to {Status}", creatorId, status);
        return creator;
    }

    /// <inheritdoc />
    public async Task<Creator> UpdateTaxFormStatusAsync(int creatorId, TaxFormStatus status, string? errorMessage = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.TaxFormStatus = status;
        creator.UpdatedAt = DateTime.UtcNow;

        // Store the error message when reverting to Pending; clear it for all other statuses
        // Truncate to column max length to prevent SQL truncation errors
        creator.LastTaxFormErrorMessage = status == TaxFormStatus.Pending
            ? errorMessage?[..Math.Min(errorMessage.Length, Creator.LastTaxFormErrorMessageMaxLength)]
            : null;

        if (status == TaxFormStatus.Completed)
        {
            creator.TaxFormCompletedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated tax form status for creator {CreatorId} to {Status}", creatorId, status);
        return creator;
    }

    /// <inheritdoc />
    public async Task<Creator> SetTinMatchFailedAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.LastTinMatchFailedAt = DateTime.UtcNow;
        creator.TaxFormStatus = TaxFormStatus.Failed;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Recorded TIN match failure for creator {CreatorId} at {Time}", creatorId, creator.LastTinMatchFailedAt);
        return creator;
    }

    /// <inheritdoc />
    public async Task<Creator> UpdateTaxFormStatusWithTaxDataAsync(
        int creatorId,
        TaxFormStatus status,
        TaxResidencyType taxResidencyType,
        string? taxResidencyCountry,
        decimal withholdingRate,
        DateTime? taxFormExpirationDate,
        Guid? taxBanditsSubmissionId,
        bool subjectToBackupWithholding = false)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        // Update tax form status
        creator.TaxFormStatus = status;
        creator.UpdatedAt = DateTime.UtcNow;

        if (status == TaxFormStatus.Completed)
        {
            creator.TaxFormCompletedAt = DateTime.UtcNow;
        }

        // Update tax residency data
        creator.TaxResidencyType = taxResidencyType;
        creator.TaxResidencyCountry = taxResidencyCountry;
        creator.WithholdingRate = withholdingRate;
        creator.TaxFormExpirationDate = taxFormExpirationDate;
        creator.TaxBanditsSubmissionId = taxBanditsSubmissionId;
        creator.SubjectToBackupWithholding = subjectToBackupWithholding;
        creator.LastVerifiedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Updated tax form status and tax data for creator {CreatorId}: Status={Status}, ResidencyType={ResidencyType}, Country={Country}, WithholdingRate={Rate:P2}, BackupWithholding={BackupWithholding}",
            creatorId, status, taxResidencyType, taxResidencyCountry, withholdingRate, subjectToBackupWithholding);
        return creator;
    }

    /// <inheritdoc />
    public async Task<Creator> UpdateTaxBanditsPayeeRefAsync(int creatorId, string payeeRef)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.TaxBanditsPayeeRef = payeeRef;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated TaxBandits PayeeRef for creator {CreatorId} to {PayeeRef}", creatorId, payeeRef);
        return creator;
    }

    /// <inheritdoc />
    public async Task<bool> IsActiveCreatorAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Creators.AnyAsync(s => s.UserId == userId && s.IsActive);
    }

    /// <inheritdoc />
    public async Task<int?> GetActiveCreatorIdAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Creators
            .Where(s => s.UserId == userId && s.IsActive)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<List<Creator>> GetActiveCreatorsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Creators
            .Include(s => s.User)
            .Where(s => s.IsActive)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Creator>> GetAllCreatorsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Creators
            .Include(s => s.User)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<Creator> UpdateCreatorProfileAsync(int creatorId, string? displayName, string? bio)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.DisplayName = displayName;
        creator.Bio = bio;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated profile for creator {CreatorId}", creatorId);
        return creator;
    }

    /// <inheritdoc />
    public async Task<Creator?> UpdateCreatorPayoutEmailAsync(int userId, string? payoutEmail, bool payPalAccountAffirmed)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FirstOrDefaultAsync(s => s.UserId == userId);
        if (creator == null)
        {
            _logger.LogWarning("Creator record not found while updating payout email for user {UserId}", userId);
            return null;
        }

        var normalizedPayoutEmail = payoutEmail?.Trim();
        var hasPayoutEmail = !string.IsNullOrWhiteSpace(normalizedPayoutEmail);

        if (!hasPayoutEmail && payPalAccountAffirmed)
        {
            throw new ArgumentException(PayoutEmailValidator.PayPalEmailRequiredForAffirmationMessage);
        }

        if (hasPayoutEmail && !payPalAccountAffirmed)
        {
            throw new ArgumentException(PayoutEmailValidator.PayPalAffirmationRequiredMessage);
        }

        if (hasPayoutEmail && !PayoutEmailValidator.IsValidPayPalEmail(normalizedPayoutEmail!))
        {
            throw new ArgumentException(PayoutEmailValidator.InvalidPayPalEmailMessage);
        }

        creator.PayPalEmail = hasPayoutEmail ? normalizedPayoutEmail : null;
        creator.PayPalAccountAffirmed = hasPayoutEmail && payPalAccountAffirmed;
        creator.PaymentsReceivable = creator.PayPalAccountAffirmed;
        creator.PrimaryEmailConfirmed = creator.PayPalAccountAffirmed;
        creator.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        _logger.LogInformation("Updated payout email for creator {CreatorId}", creator.Id);
        return creator;
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<bool> TryClaimActivationAnnouncementAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // One conditional UPDATE, not a read then a write. Two tabs opening the page at the same
        // moment cannot both win it - which matters, because the winner spends that true on a
        // Google Ads conversion and a permanent history row.
        var rows = await context.Creators
            .Where(c => c.Id == creatorId && c.ActivationAnnouncedAt == null)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(c => c.ActivationAnnouncedAt, DateTime.UtcNow));

        return rows > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimDeactivationAnnouncementAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var rows = await context.Creators
            .Where(c => c.Id == creatorId && c.DeactivationAnnouncedAt == null)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(c => c.DeactivationAnnouncedAt, DateTime.UtcNow));

        return rows > 0;
    }

    public async Task<Creator> ActivateCreatorAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.IsActive = true;
        creator.OnboardingStatus = CreatorOnboardingStatus.Completed;

        // OnboardedAt is deliberately NOT written here. StartOnboardingAsync calls
        // ResetCreatorOnboardingAsync immediately before this, and that is what stamps it - so
        // every creator already has one. Setting it again here would overwrite a correct
        // historical date with today every time a dormant creator is re-activated, which both
        // CompleteOnboardingAsync and the admin activate endpoint do.

        // Arms the one-time celebration. Null means "owed"; the page claims it on arrival.
        creator.ActivationAnnouncedAt = null;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Activated creator {CreatorId}", creatorId);

        // Notify admin about creator activation
        try
        {
            var user = await _userManager.FindByIdAsync(creator.UserId.ToString());
            if (user?.Email != null)
            {
                await _adminNotificationService.NotifyCreatorStatusGainedAsync(user.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send admin notification for creator activation {CreatorId}", creatorId);
        }

        return creator;
    }

    /// <inheritdoc />
    public async Task<Creator> DeactivateCreatorAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.IsActive = false;
        creator.OnboardingStatus = CreatorOnboardingStatus.Suspended;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Deactivated creator {CreatorId}", creatorId);

        // Notify admin about creator deactivation
        try
        {
            var user = await _userManager.FindByIdAsync(creator.UserId.ToString());
            if (user?.Email != null)
            {
                await _adminNotificationService.NotifyCreatorStatusLostAsync(user.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send admin notification for creator deactivation {CreatorId}", creatorId);
        }

        return creator;
    }

    /// <inheritdoc />
    public async Task<int?> GetCreatorIdForUserAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var creator = await context.Creators
            .Where(s => s.UserId == userId)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync();
        return creator;
    }

    /// <inheritdoc />
    public async Task<bool> StopBeingCreatorAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FirstOrDefaultAsync(s => s.UserId == userId);
        if (creator == null)
        {
            _logger.LogWarning("User {UserId} is not a creator", userId);
            return false;
        }

        // Deactivate all creator's songs
        var deactivatedCount = await DeactivateAllCreatorSongsAsync(creator.Id);
        _logger.LogInformation("Deactivated {Count} songs for creator {CreatorId}", deactivatedCount, creator.Id);

        // Delete all personas for this creator (including blob images)
        var deletedPersonaCount = await _creatorPersonaService.DeleteAllPersonasForCreatorAsync(creator.Id);
        _logger.LogInformation("Deleted {Count} personas for creator {CreatorId}", deletedPersonaCount, creator.Id);

        // Mark creator as inactive
        creator.IsActive = false;
        creator.OnboardingStatus = CreatorOnboardingStatus.Suspended;
        creator.CreatorAgreementAccepted = false;
        creator.CreatorAgreementAcceptedAtUtc = null;
        creator.DeactivationAnnouncedAt = null;
        creator.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        // Remove Creator role from the user
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user != null && await _userManager.IsInRoleAsync(user, Roles.Creator))
        {
            await _userManager.RemoveFromRoleAsync(user, Roles.Creator);
            _logger.LogInformation("Removed Creator role from user {UserId}", userId);
        }

        _logger.LogInformation("User {UserId} stopped being a creator", userId);

        // Notify admin about creator status loss
        try
        {
            if (user?.Email != null)
            {
                await _adminNotificationService.NotifyCreatorStatusLostAsync(user.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send admin notification for user {UserId} stopping being a creator", userId);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteCreatorSongAsync(int songMetadataId, int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var song = await context.SongMetadata.FindAsync(songMetadataId);
        if (song == null)
        {
            _logger.LogWarning("Song metadata {SongMetadataId} not found", songMetadataId);
            return false;
        }

        // Verify the creator owns this song
        if (song.CreatorId != creatorId)
        {
            _logger.LogWarning("Creator {CreatorId} does not own song {SongMetadataId}", creatorId, songMetadataId);
            return false;
        }

        // Delete from Azure storage
        await DeleteSongFromStorageAsync(song);

        // Mark as inactive
        song.IsActive = false;
        song.UpdatedAt = DateTime.UtcNow;

        // Remove from all user playlists
        var userPlaylists = await context.UserPlaylists
            .Where(up => up.SongMetadataId == songMetadataId)
            .ToListAsync();
        context.UserPlaylists.RemoveRange(userPlaylists);

        // Remove from all recommended playlists
        var recommendedPlaylists = await context.RecommendedPlaylists
            .Where(rp => rp.SongMetadataId == songMetadataId)
            .ToListAsync();
        context.RecommendedPlaylists.RemoveRange(recommendedPlaylists);

        await context.SaveChangesAsync();

        _logger.LogInformation("Deleted song {SongMetadataId} for creator {CreatorId}", songMetadataId, creatorId);
        return true;
    }

    /// <inheritdoc />
    public async Task<List<SongMetadata>> GetCreatorSongsAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.SongMetadata
            .Include(s => s.Creator)
                .ThenInclude(c => c.User)
            .Include(s => s.Persona)
            .Where(s => s.CreatorId == creatorId && s.IsActive)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> GetCreatorSongCountAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.SongMetadata
            .CountAsync(s => s.CreatorId == creatorId && s.IsActive);
    }

    /// <inheritdoc />
    public async Task<int> DeactivateAllCreatorSongsAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var songs = await context.SongMetadata
            .Where(s => s.CreatorId == creatorId && s.IsActive)
            .ToListAsync();

        if (songs.Count == 0)
            return 0;

        var songIds = songs.Select(s => s.Id).ToList();

        foreach (var song in songs)
        {
            // Delete from Azure storage
            await DeleteSongFromStorageAsync(song);

            // Mark as inactive (retain row for StreamPayout/SongStatusHistory/tax records)
            song.IsActive = false;
            song.UpdatedAt = DateTime.UtcNow;
        }

        // Remove related records that should not persist after creator leaves
        var userPlaylists = await context.UserPlaylists
            .Where(up => songIds.Contains(up.SongMetadataId))
            .ToListAsync();
        context.UserPlaylists.RemoveRange(userPlaylists);

        var songLikes = await context.SongLikes
            .Where(sl => songIds.Contains(sl.SongMetadataId))
            .ToListAsync();
        context.SongLikes.RemoveRange(songLikes);

        var recommendedPlaylists = await context.RecommendedPlaylists
            .Where(rp => songIds.Contains(rp.SongMetadataId))
            .ToListAsync();
        context.RecommendedPlaylists.RemoveRange(recommendedPlaylists);

        await context.SaveChangesAsync();
        return songs.Count;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeCreatorConsentAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            _logger.LogWarning("Creator {CreatorId} not found for consent revocation", creatorId);
            return false;
        }

        // Deactivate all creator's songs (marks inactive + removes from Azure storage)
        var deactivatedCount = await DeactivateAllCreatorSongsAsync(creatorId);
        _logger.LogInformation("Deactivated {Count} songs for creator {CreatorId} due to consent revocation", deactivatedCount, creatorId);

        // Delete all personas for this creator (including blob images)
        var deletedPersonaCount = await _creatorPersonaService.DeleteAllPersonasForCreatorAsync(creatorId);
        _logger.LogInformation("Deleted {Count} personas for creator {CreatorId} due to consent revocation", deletedPersonaCount, creatorId);

        // Mark creator as inactive with consent revoked status
        creator.IsActive = false;
        creator.OnboardingStatus = CreatorOnboardingStatus.ConsentRevoked;
        creator.CreatorAgreementAccepted = false;
        creator.CreatorAgreementAcceptedAtUtc = null;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Revoked consent for creator {CreatorId}", creatorId);
        return true;
    }

    /// <inheritdoc />
    public async Task<Creator> ResetCreatorOnboardingAsync(int creatorId, string? payPalEmail, bool payPalAccountAffirmed)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        // Reset all onboarding-related fields for re-signup.
        // This is critical for returning creators whose status was set to Suspended
        // when they stopped selling — previously the controller set these via direct
        // DbContext manipulation which could silently fail.
        var normalizedPayPalEmail = payPalEmail?.Trim();
        var hasPayPalEmail = !string.IsNullOrWhiteSpace(normalizedPayPalEmail);
        if (hasPayPalEmail)
        {
            if (!PayoutEmailValidator.IsValidPayPalEmail(normalizedPayPalEmail!))
            {
                throw new ArgumentException(PayoutEmailValidator.InvalidPayPalEmailMessage);
            }

            creator.PayPalEmail = normalizedPayPalEmail;
            creator.PayPalAccountAffirmed = payPalAccountAffirmed;
        }
        creator.OnboardingStatus = CreatorOnboardingStatus.Completed;
        // Preserve TaxFormStatus for returning creators who have already completed a tax form.
        // They should not be required to fill out another W8/W9 when re-signing up.
        if (creator.TaxFormCompletedAt == null)
        {
            creator.TaxFormStatus = TaxFormStatus.NotStarted;
        }
        creator.PaymentsReceivable = creator.PayPalAccountAffirmed;
        creator.PrimaryEmailConfirmed = creator.PayPalAccountAffirmed;
        creator.OnboardedAt = DateTime.UtcNow;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Reset onboarding for creator {CreatorId}: OnboardingStatus={Status}, PayPalEmail={PayPalEmail}, PayPalAffirmed={PayPalAffirmed}",
            creatorId, creator.OnboardingStatus, creator.PayPalEmail, creator.PayPalAccountAffirmed);

        return creator;
    }

    private async Task<Creator> UpdateCreatorAgreementAcceptanceAsync(int creatorId, bool creatorAgreementAccepted)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.CreatorAgreementAccepted = creatorAgreementAccepted;
        creator.CreatorAgreementAcceptedAtUtc = creatorAgreementAccepted ? DateTime.UtcNow : null;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated Creator Agreement acceptance for creator {CreatorId}: {Accepted}",
            creatorId, creatorAgreementAccepted);

        return creator;
    }

    /// <summary>
    /// Deletes a song's files from Azure storage.
    /// </summary>
    private async Task DeleteSongFromStorageAsync(SongMetadata song)
    {
        try
        {
            var paths = new[]
            {
                song.OriginalAudioBlobPath,
                song.Mp3BlobPath,
                song.ImageBlobPath,
                song.OriginalCoverArtBlobPath,
                song.BlobPath
            }
            // Both derived rather than stored, and both leak on every delete if not named here.
            // The sharing image contributes two candidates because it moved from PNG to JPEG, and
            // songs last shared before that change still have the PNG sitting in storage.
            .Concat(SongMediaPaths.FacebookImageCandidatesFor(song.ImageBlobPath))
            .Concat(ImageVariantPaths.VariantsFor(song.ImageBlobPath, ImageVariantSizes.CoverArt))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                await _storageService.DeleteAsync(path!);
                _logger.LogInformation("Deleted song blob: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting song files from storage for song {SongId}", song.Id);
            // Don't throw - we still want to mark the song as inactive even if storage deletion fails
        }
    }

    /// <inheritdoc />
    public async Task<Creator> UpdateLocationCertificationAsync(int creatorId, CreatorLocationCertification locationCertification, bool acknowledgmentAccepted)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.LocationCertification = locationCertification;
        creator.AcknowledgmentAccepted = acknowledgmentAccepted;
        creator.AcknowledgmentDateTimeUtc = DateTime.UtcNow;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated location certification for creator {CreatorId}: {Certification}, Acknowledgment: {Acknowledged}",
            creatorId, locationCertification, acknowledgmentAccepted);

        return creator;
    }

    /// <inheritdoc />
    public async Task<Creator> UpdatePayoutRequirementsAcknowledgmentAsync(int creatorId, bool payoutRequirementsAcknowledged)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.Id == creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.PayoutRequirementsAcknowledged = payoutRequirementsAcknowledged;
        creator.PayoutRequirementsAcknowledgedAtUtc = payoutRequirementsAcknowledged ? DateTime.UtcNow : null;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        if (payoutRequirementsAcknowledged && !string.IsNullOrWhiteSpace(creator.User.Email))
        {
            await _adminNotificationService.RecordUserHistoryAsync(
                creator.UserId,
                creator.User.Email,
                UserHistoryEventTypes.CreatorPayoutRequirementsAcknowledged,
                "Creator acknowledged that PayPal confirmation and completed tax forms are required before payouts.");
        }

        _logger.LogInformation("Updated payout requirements acknowledgment for creator {CreatorId}: {Acknowledged}",
            creatorId, payoutRequirementsAcknowledged);

        return creator;
    }

    /// <inheritdoc />
    public async Task<StartOnboardingResult> StartOnboardingAsync(CreatorOnboardingInput request)
    {
        var creatorAgreementAccepted = request.CreatorAgreementAccepted || request.AcknowledgmentAccepted;
        if (!creatorAgreementAccepted)
            return StartOnboardingResult.Failure("You must accept the Creator Agreement to become a creator.");

        var hasPayPalEmail = !string.IsNullOrWhiteSpace(request.PayPalEmail);
        if (hasPayPalEmail && !request.PayPalAccountAffirmed)
            return StartOnboardingResult.Failure("You must affirm that you own or are authorized to use the PayPal account before saving a payout email.");

        if (hasPayPalEmail && !PayoutEmailValidator.IsValidPayPalEmail(request.PayPalEmail))
            return StartOnboardingResult.Failure(PayoutEmailValidator.InvalidPayPalEmailMessage);

        if (!hasPayPalEmail && request.PayPalAccountAffirmed)
            return StartOnboardingResult.Failure("Please enter a PayPal payout email address before affirming your PayPal account.");

        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user == null || string.IsNullOrWhiteSpace(user.Email))
            return StartOnboardingResult.Failure("User must have a verified email address to become a creator.");

        // Check if user already has a creator record
        var existingCreator = await GetCreatorByUserIdAsync(request.UserId);
        if (existingCreator != null && existingCreator.IsActive)
            return StartOnboardingResult.Failure("You are already an active creator.");

        // Create or update creator record
        Creator creator;
        if (existingCreator == null)
        {
            creator = await CreateCreatorAsync(request.UserId, request.DisplayName, request.Bio);
        }
        else
        {
            creator = existingCreator;
            if (!string.IsNullOrWhiteSpace(request.DisplayName))
            {
                await UpdateCreatorProfileAsync(creator.Id, request.DisplayName, request.Bio);
            }
        }

        await UpdateCreatorAgreementAcceptanceAsync(creator.Id, true);

        // Preserve legacy attestation fields when older clients still submit them, but do not
        // require them for creator activation or payout readiness.
        if (request.LocationCertification != CreatorLocationCertification.None || request.AcknowledgmentAccepted)
        {
            await UpdateLocationCertificationAsync(creator.Id, request.LocationCertification, request.AcknowledgmentAccepted);
        }

        if (request.PayoutRequirementsAcknowledged)
        {
            await UpdatePayoutRequirementsAcknowledgmentAsync(creator.Id, true);
        }

        var resetCreator = await ResetCreatorOnboardingAsync(creator.Id, request.PayPalEmail, request.PayPalAccountAffirmed);

        await ActivateCreatorAsync(creator.Id);

        if (!await _userManager.IsInRoleAsync(user, Roles.Creator))
        {
            await _userManager.AddToRoleAsync(user, Roles.Creator);
            _logger.LogInformation("Added Creator role to user {UserId}", request.UserId);
        }

        try
        {
            await _creatorEmailService.SendCreatorWelcomeEmailAsync(
                user.Email,
                _configuration.GetBaseUrl(),
                resetCreator.PayPalAccountAffirmed && !string.IsNullOrWhiteSpace(resetCreator.PayPalEmail),
                resetCreator.TaxFormStatus == TaxFormStatus.Completed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send creator welcome email to user {UserId}", request.UserId);
        }

        var taxFormPending = false;
        if (request.SubmitTaxFormNow && resetCreator.TaxFormStatus != TaxFormStatus.Completed)
        {
            await UpdateTaxBanditsPayeeRefAsync(creator.Id, user.Email);
            await UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Pending);
            taxFormPending = true;
        }

        _logger.LogInformation(
            "Completed creator signup for user {UserId}. AgreementAccepted={AgreementAccepted}, PayPalEmail={PayPalEmail}, PayPalAffirmed={PayPalAffirmed}, TaxFormPending={TaxFormPending}",
            request.UserId, true, resetCreator.PayPalEmail, resetCreator.PayPalAccountAffirmed, taxFormPending);

        return new StartOnboardingResult { Success = true, IsActive = true, TaxFormPending = taxFormPending };
    }

    /// <inheritdoc />
    public async Task<CompleteOnboardingResult> CompleteOnboardingAsync(int userId)
    {
        var creator = await GetCreatorByUserIdAsync(userId);
        if (creator == null)
            return CompleteOnboardingResult.Failure("Creator record not found. Please start the onboarding process first.");

        if (creator.OnboardingStatus == CreatorOnboardingStatus.Completed &&
            (creator.CreatorAgreementAccepted || creator.AcknowledgmentAccepted))
        {
            // Activate the creator if not already active
            if (!creator.IsActive)
            {
                await ActivateCreatorAsync(creator.Id);
            }

            // Add Creator role if user doesn't already have it
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user != null && !await _userManager.IsInRoleAsync(user, Roles.Creator))
            {
                await _userManager.AddToRoleAsync(user, Roles.Creator);
                _logger.LogInformation("Added Creator role to user {UserId}", userId);
            }

            // Reload to get the latest state
            var updatedCreator = await GetCreatorByUserIdAsync(userId);

            return new CompleteOnboardingResult
            {
                Success = true,
                IsActive = updatedCreator?.IsActive ?? false,
                PaymentsReceivable = updatedCreator?.PaymentsReceivable ?? false,
                PrimaryEmailConfirmed = updatedCreator?.PrimaryEmailConfirmed ?? false
            };
        }

        _logger.LogInformation("Checked creator onboarding status for user {UserId}, IsActive: {IsActive}", userId, creator.IsActive);

        return new CompleteOnboardingResult
        {
            Success = true,
            IsActive = creator.IsActive,
            PaymentsReceivable = creator.PaymentsReceivable,
            PrimaryEmailConfirmed = creator.PrimaryEmailConfirmed
        };
    }

    /// <inheritdoc />
    public async Task<InitiateTaxFormUpdateResult> InitiateTaxFormUpdateAsync(int userId, string? email)
    {
        var creator = await GetCreatorByUserIdAsync(userId);
        if (creator == null || !creator.IsActive)
            return InitiateTaxFormUpdateResult.Failure("You must be an active creator to update your tax form.");

        // Set tax form status to Pending so the embedded form page can load
        await UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Pending);

        // Update the PayeeRef (email) for the new request
        if (!string.IsNullOrWhiteSpace(email))
        {
            await UpdateTaxBanditsPayeeRefAsync(creator.Id, email);
        }

        _logger.LogInformation("Creator {CreatorId} initiated tax form update for user {UserId}", creator.Id, userId);

        return new InitiateTaxFormUpdateResult { Success = true };
    }
}
