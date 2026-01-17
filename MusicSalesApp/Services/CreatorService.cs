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
    public async Task<Creator?> GetCreatorByMerchantIdAsync(string merchantId)
    {
        if (string.IsNullOrWhiteSpace(merchantId))
            return null;

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Creators
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.PayPalMerchantId == merchantId);
    }

    /// <inheritdoc />
    public async Task<Creator?> GetCreatorByTrackingIdAsync(string trackingId)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
            return null;

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Creators
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.PayPalTrackingId == trackingId);
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
    public async Task<Creator> UpdateOnboardingInfoAsync(int creatorId, string trackingId, string referralUrl)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        creator.PayPalTrackingId = trackingId;
        creator.PayPalReferralUrl = referralUrl;
        creator.OnboardingStatus = CreatorOnboardingStatus.Pending;
        creator.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated onboarding info for creator {CreatorId} with tracking ID {TrackingId}", creatorId, trackingId);
        return creator;
    }

    /// <inheritdoc />
    public async Task<Creator> CompleteOnboardingAsync(int creatorId, string merchantId, bool paymentsReceivable, bool primaryEmailConfirmed)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creator = await context.Creators.FindAsync(creatorId);
        if (creator == null)
        {
            throw new InvalidOperationException($"Creator with ID {creatorId} not found");
        }

        // In sandbox mode, having a valid merchant_id is sufficient for PayPal onboarding completion
        // since sandbox accounts may not have payments_receivable or primary_email_confirmed set
        var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
        var sandboxMode = _configuration.GetValue<bool>("PayPal:SandboxMode", baseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase));
        
        // Determine if PayPal onboarding is complete:
        // - In production: requires both paymentsReceivable and primaryEmailConfirmed
        // - In sandbox: having a valid merchantId is sufficient (optional override via PayPal:SandboxAllowPartialOnboarding)
        var allowPartialOnboarding = sandboxMode && _configuration.GetValue<bool>("PayPal:SandboxAllowPartialOnboarding", true);
        var isPayPalComplete = (paymentsReceivable && primaryEmailConfirmed) || 
                               (allowPartialOnboarding && !string.IsNullOrWhiteSpace(merchantId));

        creator.PayPalMerchantId = merchantId;
        creator.PaymentsReceivable = paymentsReceivable;
        creator.PrimaryEmailConfirmed = primaryEmailConfirmed;
        creator.OnboardingStatus = isPayPalComplete
            ? CreatorOnboardingStatus.Completed
            : CreatorOnboardingStatus.InProgress;
        creator.OnboardedAt = isPayPalComplete ? DateTime.UtcNow : null;
        creator.UpdatedAt = DateTime.UtcNow;
        
        // Note: IsActive is NOT set here. Creator only becomes active when BOTH
        // PayPal onboarding AND tax form (W-9/W-8) are complete.
        // The webhooks from PayPal and TaxBandits will handle setting IsActive.

        await context.SaveChangesAsync();

        _logger.LogInformation("Completed PayPal onboarding for creator {CreatorId} with merchant ID {MerchantId}, PayPalStatus: {PayPalStatus}, TaxFormStatus: {TaxFormStatus}, SandboxMode: {SandboxMode}, PaymentsReceivable: {PaymentsReceivable}, PrimaryEmailConfirmed: {PrimaryEmailConfirmed}",
            creatorId, merchantId, creator.OnboardingStatus, creator.TaxFormStatus, sandboxMode, paymentsReceivable, primaryEmailConfirmed);
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

        foreach (var song in songs)
        {
            // Delete from Azure storage
            await DeleteSongFromStorageAsync(song);

            // Mark as inactive
            song.IsActive = false;
            song.UpdatedAt = DateTime.UtcNow;
        }

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
}
