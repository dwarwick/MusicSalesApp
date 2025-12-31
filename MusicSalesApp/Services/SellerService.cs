#nullable enable
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing seller accounts and their operations.
/// </summary>
public class SellerService : ISellerService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IAzureStorageService _storageService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<SellerService> _logger;

    public SellerService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IAzureStorageService storageService,
        UserManager<ApplicationUser> userManager,
        ILogger<SellerService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _storageService = storageService;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Seller?> GetSellerByIdAsync(int sellerId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Sellers
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Id == sellerId);
    }

    /// <inheritdoc />
    public async Task<Seller?> GetSellerByUserIdAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Sellers
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.UserId == userId);
    }

    /// <inheritdoc />
    public async Task<Seller?> GetSellerByMerchantIdAsync(string merchantId)
    {
        if (string.IsNullOrWhiteSpace(merchantId))
            return null;

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Sellers
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.PayPalMerchantId == merchantId);
    }

    /// <inheritdoc />
    public async Task<Seller?> GetSellerByTrackingIdAsync(string trackingId)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
            return null;

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Sellers
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.PayPalTrackingId == trackingId);
    }

    /// <inheritdoc />
    public async Task<Seller> CreateSellerAsync(int userId, string? displayName = null, string? bio = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Check if seller already exists
        var existingSeller = await context.Sellers.FirstOrDefaultAsync(s => s.UserId == userId);
        if (existingSeller != null)
        {
            _logger.LogWarning("Attempt to create duplicate seller record for user {UserId}", userId);
            return existingSeller;
        }

        var seller = new Seller
        {
            UserId = userId,
            DisplayName = displayName,
            Bio = bio,
            OnboardingStatus = SellerOnboardingStatus.NotStarted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Sellers.Add(seller);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created seller record for user {UserId}", userId);
        return seller;
    }

    /// <inheritdoc />
    public async Task<Seller> UpdateOnboardingInfoAsync(int sellerId, string trackingId, string referralUrl)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var seller = await context.Sellers.FindAsync(sellerId);
        if (seller == null)
        {
            throw new InvalidOperationException($"Seller with ID {sellerId} not found");
        }

        seller.PayPalTrackingId = trackingId;
        seller.PayPalReferralUrl = referralUrl;
        seller.OnboardingStatus = SellerOnboardingStatus.Pending;
        seller.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated onboarding info for seller {SellerId} with tracking ID {TrackingId}", sellerId, trackingId);
        return seller;
    }

    /// <inheritdoc />
    public async Task<Seller> CompleteOnboardingAsync(int sellerId, string merchantId, bool paymentsReceivable, bool primaryEmailConfirmed)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var seller = await context.Sellers.FindAsync(sellerId);
        if (seller == null)
        {
            throw new InvalidOperationException($"Seller with ID {sellerId} not found");
        }

        seller.PayPalMerchantId = merchantId;
        seller.PaymentsReceivable = paymentsReceivable;
        seller.PrimaryEmailConfirmed = primaryEmailConfirmed;
        seller.OnboardingStatus = paymentsReceivable && primaryEmailConfirmed
            ? SellerOnboardingStatus.Completed
            : SellerOnboardingStatus.InProgress;
        seller.OnboardedAt = paymentsReceivable && primaryEmailConfirmed ? DateTime.UtcNow : null;
        seller.IsActive = paymentsReceivable && primaryEmailConfirmed;
        seller.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Completed onboarding for seller {SellerId} with merchant ID {MerchantId}, IsActive: {IsActive}",
            sellerId, merchantId, seller.IsActive);
        return seller;
    }

    /// <inheritdoc />
    public async Task<Seller> UpdateOnboardingStatusAsync(int sellerId, SellerOnboardingStatus status)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var seller = await context.Sellers.FindAsync(sellerId);
        if (seller == null)
        {
            throw new InvalidOperationException($"Seller with ID {sellerId} not found");
        }

        seller.OnboardingStatus = status;
        seller.UpdatedAt = DateTime.UtcNow;

        if (status == SellerOnboardingStatus.Failed || status == SellerOnboardingStatus.Suspended)
        {
            seller.IsActive = false;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated onboarding status for seller {SellerId} to {Status}", sellerId, status);
        return seller;
    }

    /// <inheritdoc />
    public async Task<bool> IsActiveSellerAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Sellers.AnyAsync(s => s.UserId == userId && s.IsActive);
    }

    /// <inheritdoc />
    public async Task<List<Seller>> GetActiveSellersAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Sellers
            .Include(s => s.User)
            .Where(s => s.IsActive)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Seller>> GetAllSellersAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Sellers
            .Include(s => s.User)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<Seller> UpdateSellerProfileAsync(int sellerId, string? displayName, string? bio)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var seller = await context.Sellers.FindAsync(sellerId);
        if (seller == null)
        {
            throw new InvalidOperationException($"Seller with ID {sellerId} not found");
        }

        seller.DisplayName = displayName;
        seller.Bio = bio;
        seller.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Updated profile for seller {SellerId}", sellerId);
        return seller;
    }

    /// <inheritdoc />
    public async Task<Seller> ActivateSellerAsync(int sellerId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var seller = await context.Sellers.FindAsync(sellerId);
        if (seller == null)
        {
            throw new InvalidOperationException($"Seller with ID {sellerId} not found");
        }

        seller.IsActive = true;
        seller.OnboardingStatus = SellerOnboardingStatus.Completed;
        seller.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Activated seller {SellerId}", sellerId);
        return seller;
    }

    /// <inheritdoc />
    public async Task<Seller> DeactivateSellerAsync(int sellerId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var seller = await context.Sellers.FindAsync(sellerId);
        if (seller == null)
        {
            throw new InvalidOperationException($"Seller with ID {sellerId} not found");
        }

        seller.IsActive = false;
        seller.OnboardingStatus = SellerOnboardingStatus.Suspended;
        seller.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("Deactivated seller {SellerId}", sellerId);
        return seller;
    }

    /// <inheritdoc />
    public async Task<int?> GetSellerIdForUserAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var seller = await context.Sellers
            .Where(s => s.UserId == userId)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync();
        return seller;
    }

    /// <inheritdoc />
    public async Task<bool> StopBeingSellerAsync(int userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var seller = await context.Sellers.FirstOrDefaultAsync(s => s.UserId == userId);
        if (seller == null)
        {
            _logger.LogWarning("User {UserId} is not a seller", userId);
            return false;
        }

        // Deactivate all seller's songs
        var deactivatedCount = await DeactivateAllSellerSongsAsync(seller.Id);
        _logger.LogInformation("Deactivated {Count} songs for seller {SellerId}", deactivatedCount, seller.Id);

        // Mark seller as inactive
        seller.IsActive = false;
        seller.OnboardingStatus = SellerOnboardingStatus.Suspended;
        seller.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        // Remove Seller role from the user
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user != null && await _userManager.IsInRoleAsync(user, Roles.Seller))
        {
            await _userManager.RemoveFromRoleAsync(user, Roles.Seller);
            _logger.LogInformation("Removed Seller role from user {UserId}", userId);
        }

        _logger.LogInformation("User {UserId} stopped being a seller", userId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSellerSongAsync(int songMetadataId, int sellerId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var song = await context.SongMetadata.FindAsync(songMetadataId);
        if (song == null)
        {
            _logger.LogWarning("Song metadata {SongMetadataId} not found", songMetadataId);
            return false;
        }

        // Verify the seller owns this song
        if (song.SellerId != sellerId)
        {
            _logger.LogWarning("Seller {SellerId} does not own song {SongMetadataId}", sellerId, songMetadataId);
            return false;
        }

        // Delete from Azure storage
        await DeleteSongFromStorageAsync(song);

        // Mark as inactive
        song.IsActive = false;
        song.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        _logger.LogInformation("Deleted song {SongMetadataId} for seller {SellerId}", songMetadataId, sellerId);
        return true;
    }

    /// <inheritdoc />
    public async Task<List<SongMetadata>> GetSellerSongsAsync(int sellerId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.SongMetadata
            .Where(s => s.SellerId == sellerId && s.IsActive)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> DeactivateAllSellerSongsAsync(int sellerId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var songs = await context.SongMetadata
            .Where(s => s.SellerId == sellerId && s.IsActive)
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
