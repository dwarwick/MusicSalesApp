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

    public CreatorService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IAzureStorageService storageService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<CreatorService> logger,
        IAppSettingsService appSettingsService)
    {
        _dbContextFactory = dbContextFactory;
        _storageService = storageService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
        _appSettingsService = appSettingsService;
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

        var creator = new Creator
        {
            UserId = userId,
            DisplayName = displayName,
            Bio = bio,
            StreamPayRate = streamPayRate, // Set from app settings
            OnboardingStatus = CreatorOnboardingStatus.NotStarted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created creator record for user {UserId} with stream pay rate ${StreamRate:F6} per stream", 
            userId, streamPayRate);
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
    public async Task<Creator> UpdateTaxFormStatusAsync(int creatorId, TaxFormStatus status)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.TaxFormStatus = status;
        creator.UpdatedAt = DateTime.UtcNow;

        if (status == TaxFormStatus.Completed)
        {
            creator.TaxFormCompletedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated tax form status for creator {CreatorId} to {Status}", creatorId, status);
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
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Activated creator {CreatorId}", creatorId);
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

        // Mark creator as inactive
        creator.IsActive = false;
        creator.OnboardingStatus = CreatorOnboardingStatus.Suspended;
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
            .Where(s => s.CreatorId == creatorId && s.IsActive)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
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

        // Mark creator as inactive with consent revoked status
        creator.IsActive = false;
        creator.OnboardingStatus = CreatorOnboardingStatus.ConsentRevoked;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Revoked consent for creator {CreatorId}", creatorId);
        return true;
    }

    /// <inheritdoc />
    public async Task<Creator> ResetCreatorOnboardingAsync(int creatorId, string payPalEmail, bool payPalAccountAffirmed)
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
        creator.PayPalEmail = payPalEmail;
        creator.PayPalAccountAffirmed = payPalAccountAffirmed;
        creator.OnboardingStatus = CreatorOnboardingStatus.Completed;
        creator.PaymentsReceivable = true;
        creator.PrimaryEmailConfirmed = true;
        creator.OnboardedAt = DateTime.UtcNow;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Reset onboarding for creator {CreatorId}: OnboardingStatus={Status}, PayPalEmail={PayPalEmail}",
            creatorId, creator.OnboardingStatus, payPalEmail);

        return creator;
    }

    /// <summary>
    /// Deletes a song's files from Azure storage.
    /// </summary>
    private async Task DeleteSongFromStorageAsync(SongMetadata song)
    {
        try
        {
            // Delete MP3 file if exists
            if (!string.IsNullOrWhiteSpace(song.Mp3BlobPath))
            {
                await _storageService.DeleteAsync(song.Mp3BlobPath);
                _logger.LogInformation("Deleted MP3 file: {Path}", song.Mp3BlobPath);
            }

            // Delete image file if exists
            if (!string.IsNullOrWhiteSpace(song.ImageBlobPath))
            {
                await _storageService.DeleteAsync(song.ImageBlobPath);
                _logger.LogInformation("Deleted image file: {Path}", song.ImageBlobPath);
            }

            // Also try deleting from deprecated BlobPath
            if (!string.IsNullOrWhiteSpace(song.BlobPath) && 
                !string.Equals(song.BlobPath, song.Mp3BlobPath, StringComparison.Ordinal) && 
                !string.Equals(song.BlobPath, song.ImageBlobPath, StringComparison.Ordinal))
            {
                await _storageService.DeleteAsync(song.BlobPath);
                _logger.LogInformation("Deleted blob file: {Path}", song.BlobPath);
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
}
