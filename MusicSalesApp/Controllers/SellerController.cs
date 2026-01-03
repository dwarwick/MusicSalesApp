#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// API controller for seller onboarding and management operations.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Admin,User,Seller")]
public class SellerController : ControllerBase
{
    private readonly ISellerService _sellerService;
    private readonly IPayPalPartnerService _payPalPartnerService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly ILogger<SellerController> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public SellerController(
        ISellerService sellerService,
        IPayPalPartnerService payPalPartnerService,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        ILogger<SellerController> logger,
        IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _sellerService = sellerService;
        _payPalPartnerService = payPalPartnerService;
        _userManager = userManager;
        _roleManager = roleManager;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Gets the current user's seller status and information.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetSellerStatus()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var seller = await _sellerService.GetSellerByUserIdAsync(user.Id);

        if (seller == null)
        {
            return Ok(new SellerStatusResponse
            {
                IsSeller = false,
                OnboardingStatus = null,
                IsActive = false
            });
        }

        return Ok(new SellerStatusResponse
        {
            IsSeller = true,
            SellerId = seller.Id,
            OnboardingStatus = seller.OnboardingStatus.ToString(),
            IsActive = seller.IsActive,
            DisplayName = seller.DisplayName,
            Bio = seller.Bio,
            PaymentsReceivable = seller.PaymentsReceivable,
            PrimaryEmailConfirmed = seller.PrimaryEmailConfirmed,
            CommissionRate = seller.CommissionRate,
            OnboardedAt = seller.OnboardedAt,
            ReferralUrl = seller.OnboardingStatus == SellerOnboardingStatus.Pending ? seller.PayPalReferralUrl : null
        });
    }

    /// <summary>
    /// Starts the seller onboarding process by creating a PayPal partner referral.
    /// </summary>
    [HttpPost("start-onboarding")]
    public async Task<IActionResult> StartOnboarding([FromBody] StartOnboardingRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return BadRequest("User must have a verified email address to become a seller.");
        }

        // Validate PayPal email is provided
        if (string.IsNullOrWhiteSpace(request.PayPalEmail))
        {
            return BadRequest("PayPal email address is required to become a seller.");
        }

        // Check if user already has a seller record
        var existingSeller = await _sellerService.GetSellerByUserIdAsync(user.Id);
        if (existingSeller != null && existingSeller.IsActive)
        {
            return BadRequest("You are already an active seller.");
        }

        // Create or update seller record
        Seller seller;
        if (existingSeller == null)
        {
            seller = await _sellerService.CreateSellerAsync(user.Id, request.DisplayName, request.Bio);
        }
        else
        {
            seller = existingSeller;
            if (!string.IsNullOrWhiteSpace(request.DisplayName))
            {
                await _sellerService.UpdateSellerProfileAsync(seller.Id, request.DisplayName, request.Bio);
                seller.DisplayName = request.DisplayName;
                seller.Bio = request.Bio;
            }
        }

        // Update seller with PayPal email
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var sellerToUpdate = await context.Sellers.FindAsync(seller.Id);
        if (sellerToUpdate != null)
        {
            sellerToUpdate.PayPalEmail = request.PayPalEmail;
            sellerToUpdate.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // Create PayPal partner referral
        var referralResult = await _payPalPartnerService.CreatePartnerReferralAsync(user.Id, user.Email);
        if (referralResult == null || !referralResult.Success)
        {
            _logger.LogError("Failed to create partner referral for user {UserId}: {Error}", user.Id, referralResult?.ErrorMessage);
            return StatusCode(500, new { error = referralResult?.ErrorMessage ?? "Failed to create PayPal referral" });
        }

        // Update seller with onboarding info
        await _sellerService.UpdateOnboardingInfoAsync(seller.Id, referralResult.TrackingId, referralResult.ReferralUrl);

        _logger.LogInformation("Started seller onboarding for user {UserId}, tracking ID: {TrackingId}, PayPal email: {PayPalEmail}", 
            user.Id, referralResult.TrackingId, request.PayPalEmail);

        return Ok(new StartOnboardingResponse
        {
            Success = true,
            ReferralUrl = referralResult.ReferralUrl,
            TrackingId = referralResult.TrackingId
        });
    }

    /// <summary>
    /// Completes the seller onboarding after the user returns from PayPal.
    /// </summary>
    [HttpPost("complete-onboarding")]
    public async Task<IActionResult> CompleteOnboarding([FromBody] CompleteOnboardingRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var seller = await _sellerService.GetSellerByUserIdAsync(user.Id);
        if (seller == null)
        {
            return BadRequest("Seller record not found. Please start the onboarding process first.");
        }

        if (string.IsNullOrWhiteSpace(seller.PayPalTrackingId))
        {
            return BadRequest("No pending onboarding found.");
        }

        // Get the merchant status from PayPal
        var merchantStatus = await _payPalPartnerService.GetMerchantStatusByTrackingIdAsync(seller.PayPalTrackingId);
        if (merchantStatus == null)
        {
            _logger.LogWarning("Could not retrieve merchant status for tracking ID {TrackingId}", seller.PayPalTrackingId);
            
            // If we can't get status but have a merchant ID from callback, try using that
            if (!string.IsNullOrWhiteSpace(request.MerchantId))
            {
                await _sellerService.CompleteOnboardingAsync(seller.Id, request.MerchantId, true, true);
            }
            else
            {
                return BadRequest("Could not verify PayPal onboarding status. Please try again.");
            }
        }
        else
        {
            // Complete onboarding with the status from PayPal
            await _sellerService.CompleteOnboardingAsync(
                seller.Id, 
                merchantStatus.MerchantId, 
                merchantStatus.PaymentsReceivable, 
                merchantStatus.PrimaryEmailConfirmed);
        }

        // Reload seller to get updated status
        seller = await _sellerService.GetSellerByUserIdAsync(user.Id);

        // If onboarding is complete, add Seller role to user
        if (seller != null && seller.IsActive)
        {
            // Ensure the Seller role exists
            if (!await _roleManager.RoleExistsAsync(Roles.Seller))
            {
                await _roleManager.CreateAsync(new IdentityRole<int> { Name = Roles.Seller, NormalizedName = Roles.Seller.ToUpper() });
            }

            // Add Seller role if user doesn't already have it
            if (!await _userManager.IsInRoleAsync(user, Roles.Seller))
            {
                await _userManager.AddToRoleAsync(user, Roles.Seller);
                _logger.LogInformation("Added Seller role to user {UserId}", user.Id);
            }
        }

        _logger.LogInformation("Completed seller onboarding for user {UserId}, IsActive: {IsActive}", user.Id, seller?.IsActive);

        return Ok(new CompleteOnboardingResponse
        {
            Success = true,
            IsActive = seller?.IsActive ?? false,
            PaymentsReceivable = seller?.PaymentsReceivable ?? false,
            PrimaryEmailConfirmed = seller?.PrimaryEmailConfirmed ?? false
        });
    }

    /// <summary>
    /// Updates the seller's profile information.
    /// </summary>
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var seller = await _sellerService.GetSellerByUserIdAsync(user.Id);
        if (seller == null)
        {
            return NotFound("Seller record not found.");
        }

        await _sellerService.UpdateSellerProfileAsync(seller.Id, request.DisplayName, request.Bio);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Gets all sellers (admin only).
    /// </summary>
    [HttpGet("all")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllSellers()
    {
        var sellers = await _sellerService.GetAllSellersAsync();

        return Ok(sellers.Select(s => new SellerListItem
        {
            Id = s.Id,
            UserId = s.UserId,
            UserEmail = s.User?.Email ?? "Unknown",
            UserName = s.User?.UserName ?? "Unknown",
            DisplayName = s.DisplayName,
            OnboardingStatus = s.OnboardingStatus.ToString(),
            IsActive = s.IsActive,
            PaymentsReceivable = s.PaymentsReceivable,
            PrimaryEmailConfirmed = s.PrimaryEmailConfirmed,
            CommissionRate = s.CommissionRate,
            CreatedAt = s.CreatedAt,
            OnboardedAt = s.OnboardedAt
        }));
    }

    /// <summary>
    /// Activates a seller account (admin only).
    /// </summary>
    [HttpPost("{sellerId}/activate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ActivateSeller(int sellerId)
    {
        try
        {
            var seller = await _sellerService.ActivateSellerAsync(sellerId);
            
            // Add Seller role to the user
            var user = await _userManager.FindByIdAsync(seller.UserId.ToString());
            if (user != null && !await _userManager.IsInRoleAsync(user, Roles.Seller))
            {
                await _userManager.AddToRoleAsync(user, Roles.Seller);
            }

            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Deactivates a seller account (admin only).
    /// </summary>
    [HttpPost("{sellerId}/deactivate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeactivateSeller(int sellerId)
    {
        try
        {
            var seller = await _sellerService.DeactivateSellerAsync(sellerId);

            // Remove Seller role from the user
            var user = await _userManager.FindByIdAsync(seller.UserId.ToString());
            if (user != null && await _userManager.IsInRoleAsync(user, Roles.Seller))
            {
                await _userManager.RemoveFromRoleAsync(user, Roles.Seller);
            }

            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Updates a seller's commission rate (admin only).
    /// </summary>
    [HttpPut("{sellerId}/commission")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateCommissionRate(int sellerId, [FromBody] UpdateCommissionRequest request)
    {
        if (request.CommissionRate < 0 || request.CommissionRate > 1)
        {
            return BadRequest("Commission rate must be between 0 and 1 (0% to 100%).");
        }

        // TODO: Implement commission rate update in SellerService
        return Ok(new { success = true });
    }

    /// <summary>
    /// Allows a user to stop being a seller. This will remove all their music from Azure storage
    /// and mark it as inactive in the database.
    /// </summary>
    [HttpPost("stop-selling")]
    public async Task<IActionResult> StopSelling()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var success = await _sellerService.StopBeingSellerAsync(user.Id);
        if (!success)
        {
            return BadRequest("You are not currently a seller or there was an error processing your request.");
        }

        _logger.LogInformation("User {UserId} stopped being a seller", user.Id);
        return Ok(new { success = true, message = "You are no longer a seller. All your music has been removed." });
    }

    /// <summary>
    /// Deletes a specific song (marks it inactive and removes from Azure storage).
    /// </summary>
    [HttpDelete("songs/{songMetadataId}")]
    public async Task<IActionResult> DeleteSong(int songMetadataId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var seller = await _sellerService.GetSellerByUserIdAsync(user.Id);
        if (seller == null || !seller.IsActive)
        {
            return BadRequest("You are not an active seller.");
        }

        var success = await _sellerService.DeleteSellerSongAsync(songMetadataId, seller.Id);
        if (!success)
        {
            return BadRequest("Song not found or you do not have permission to delete it.");
        }

        _logger.LogInformation("Seller {SellerId} deleted song {SongMetadataId}", seller.Id, songMetadataId);
        return Ok(new { success = true, message = "Song has been deleted." });
    }

    /// <summary>
    /// Gets all songs owned by the current seller.
    /// </summary>
    [HttpGet("songs")]
    public async Task<IActionResult> GetMySongs()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var seller = await _sellerService.GetSellerByUserIdAsync(user.Id);
        if (seller == null)
        {
            return Ok(new List<object>());
        }

        var songs = await _sellerService.GetSellerSongsAsync(seller.Id);
        return Ok(songs.Select(s => new SellerSongItem
        {
            Id = s.Id,
            Mp3BlobPath = s.Mp3BlobPath,
            ImageBlobPath = s.ImageBlobPath,
            AlbumName = s.AlbumName,
            IsAlbumCover = s.IsAlbumCover,
            SongPrice = s.SongPrice,
            AlbumPrice = s.AlbumPrice,
            Genre = s.Genre,
            TrackNumber = s.TrackNumber,
            TrackLength = s.TrackLength,
            NumberOfStreams = s.NumberOfStreams,
            CreatedAt = s.CreatedAt
        }));
    }
}

#region Request/Response Models

public class SellerStatusResponse
{
    public bool IsSeller { get; set; }
    public int? SellerId { get; set; }
    public string? OnboardingStatus { get; set; }
    public bool IsActive { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public bool PaymentsReceivable { get; set; }
    public bool PrimaryEmailConfirmed { get; set; }
    public decimal CommissionRate { get; set; }
    public DateTime? OnboardedAt { get; set; }
    public string? ReferralUrl { get; set; }
}

public class StartOnboardingRequest
{
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? PayPalEmail { get; set; }
}

public class StartOnboardingResponse
{
    public bool Success { get; set; }
    public string ReferralUrl { get; set; } = string.Empty;
    public string TrackingId { get; set; } = string.Empty;
}

public class CompleteOnboardingRequest
{
    public string? MerchantId { get; set; }
}

public class CompleteOnboardingResponse
{
    public bool Success { get; set; }
    public bool IsActive { get; set; }
    public bool PaymentsReceivable { get; set; }
    public bool PrimaryEmailConfirmed { get; set; }
}

public class UpdateProfileRequest
{
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
}

public class SellerListItem
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string OnboardingStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool PaymentsReceivable { get; set; }
    public bool PrimaryEmailConfirmed { get; set; }
    public decimal CommissionRate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? OnboardedAt { get; set; }
}

public class UpdateCommissionRequest
{
    public decimal CommissionRate { get; set; }
}

public class SellerSongItem
{
    public int Id { get; set; }
    public string? Mp3BlobPath { get; set; }
    public string? ImageBlobPath { get; set; }
    public string? AlbumName { get; set; }
    public bool IsAlbumCover { get; set; }
    public decimal? SongPrice { get; set; }
    public decimal? AlbumPrice { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    public double? TrackLength { get; set; }
    public int NumberOfStreams { get; set; }
    public DateTime CreatedAt { get; set; }
}

#endregion
