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
/// API controller for creator onboarding and management operations.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Admin,User,Creator")]
public class CreatorController : ControllerBase
{
    private readonly ICreatorService _creatorService;
    private readonly IPayPalPartnerService _payPalPartnerService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly ILogger<CreatorController> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ITaxBanditsService _taxBanditsService;
    private readonly IConfiguration _configuration;

    public CreatorController(
        ICreatorService creatorService,
        IPayPalPartnerService payPalPartnerService,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        ILogger<CreatorController> logger,
        IDbContextFactory<AppDbContext> dbContextFactory,
        ITaxBanditsService taxBanditsService,
        IConfiguration configuration)
    {
        _creatorService = creatorService;
        _payPalPartnerService = payPalPartnerService;
        _userManager = userManager;
        _roleManager = roleManager;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _taxBanditsService = taxBanditsService;
        _configuration = configuration;
    }

    /// <summary>
    /// Gets the current user's creator status and information.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetCreatorStatus()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);

        if (creator == null)
        {
            return Ok(new CreatorStatusResponse
            {
                IsCreator = false,
                OnboardingStatus = null,
                IsActive = false
            });
        }

        return Ok(new CreatorStatusResponse
        {
            IsCreator = true,
            CreatorId = creator.Id,
            OnboardingStatus = creator.OnboardingStatus.ToString(),
            IsActive = creator.IsActive,
            DisplayName = creator.DisplayName,
            Bio = creator.Bio,
            PaymentsReceivable = creator.PaymentsReceivable,
            PrimaryEmailConfirmed = creator.PrimaryEmailConfirmed,
            CommissionRate = creator.CommissionRate,
            OnboardedAt = creator.OnboardedAt,
            ReferralUrl = creator.OnboardingStatus == CreatorOnboardingStatus.Pending ? creator.PayPalReferralUrl : null
        });
    }

    /// <summary>
    /// Starts the creator onboarding process by creating a PayPal partner referral.
    /// </summary>
    [HttpPost("start-onboarding")]
    public async Task<IActionResult> StartOnboarding([FromBody] StartOnboardingRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return BadRequest("User must have a verified email address to become a creator.");
        }

        // Validate PayPal email is provided
        if (string.IsNullOrWhiteSpace(request.PayPalEmail))
        {
            return BadRequest("PayPal email address is required to become a creator.");
        }

        // Check if user already has a creator record
        var existingCreator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        if (existingCreator != null && existingCreator.IsActive)
        {
            return BadRequest("You are already an active creator.");
        }

        // Create or update creator record
        Creator creator;
        if (existingCreator == null)
        {
            creator = await _creatorService.CreateCreatorAsync(user.Id, request.DisplayName, request.Bio);
        }
        else
        {
            creator = existingCreator;
            if (!string.IsNullOrWhiteSpace(request.DisplayName))
            {
                await _creatorService.UpdateCreatorProfileAsync(creator.Id, request.DisplayName, request.Bio);
                creator.DisplayName = request.DisplayName;
                creator.Bio = request.Bio;
            }
        }

        // Update creator with PayPal email
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var creatorToUpdate = await context.Creators.FindAsync(creator.Id);
        if (creatorToUpdate != null)
        {
            creatorToUpdate.PayPalEmail = request.PayPalEmail;
            creatorToUpdate.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // Create PayPal partner referral
        var referralResult = await _payPalPartnerService.CreatePartnerReferralAsync(user.Id, user.Email);
        if (referralResult == null || !referralResult.Success)
        {
            _logger.LogError("Failed to create partner referral for user {UserId}: {Error}", user.Id, referralResult?.ErrorMessage);
            return StatusCode(500, new { error = referralResult?.ErrorMessage ?? "Failed to create PayPal referral" });
        }

        // Update creator with onboarding info
        await _creatorService.UpdateOnboardingInfoAsync(creator.Id, referralResult.TrackingId, referralResult.ReferralUrl);

        // Request W-9/W-8 tax form from TaxBandits
        // The user will receive an email from our system and then from TaxBandits with a link to complete the form
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        try
        {
            // Store the PayeeRef (email) used for the W-9 request
            // Note: user.Email is already validated as non-empty at the start of this method
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                await _creatorService.UpdateTaxBanditsPayeeRefAsync(creator.Id, user.Email);
            }
            
            var w9Result = await _taxBanditsService.RequestW9ByEmailAsync(user.Id, user.Email, baseUrl);
            if (w9Result.Success)
            {
                _logger.LogInformation("W-9/W-8 request initiated for user {UserId}", user.Id);
            }
            else
            {
                // Log the error but don't fail the onboarding - admin will be notified via email
                _logger.LogWarning("W-9/W-8 request failed for user {UserId}: {Error}", user.Id, w9Result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            // Log the error but don't fail the onboarding - the W-9 can be requested again later
            _logger.LogError(ex, "Exception while requesting W-9/W-8 for user {UserId}", user.Id);
        }

        _logger.LogInformation("Started creator onboarding for user {UserId}, tracking ID: {TrackingId}, PayPal email: {PayPalEmail}", 
            user.Id, referralResult.TrackingId, request.PayPalEmail);

        return Ok(new StartOnboardingResponse
        {
            Success = true,
            ReferralUrl = referralResult.ReferralUrl,
            TrackingId = referralResult.TrackingId
        });
    }

    /// <summary>
    /// Completes the creator onboarding after the user returns from PayPal.
    /// </summary>
    [HttpPost("complete-onboarding")]
    public async Task<IActionResult> CompleteOnboarding([FromBody] CompleteOnboardingRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        if (creator == null)
        {
            return BadRequest("Creator record not found. Please start the onboarding process first.");
        }

        if (string.IsNullOrWhiteSpace(creator.PayPalTrackingId))
        {
            return BadRequest("No pending onboarding found.");
        }

        // Get the merchant status from PayPal
        var merchantStatus = await _payPalPartnerService.GetMerchantStatusByTrackingIdAsync(creator.PayPalTrackingId);
        if (merchantStatus == null)
        {
            _logger.LogWarning("Could not retrieve merchant status for tracking ID {TrackingId}", creator.PayPalTrackingId);
            
            // If we can't get status but have a merchant ID from callback, try using that
            if (!string.IsNullOrWhiteSpace(request.MerchantId))
            {
                await _creatorService.CompleteOnboardingAsync(creator.Id, request.MerchantId, true, true);
            }
            else
            {
                return BadRequest("Could not verify PayPal onboarding status. Please try again.");
            }
        }
        else
        {
            // Complete onboarding with the status from PayPal
            await _creatorService.CompleteOnboardingAsync(
                creator.Id, 
                merchantStatus.MerchantId, 
                merchantStatus.PaymentsReceivable, 
                merchantStatus.PrimaryEmailConfirmed);
        }

        // Reload creator to get updated status
        creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);

        // If onboarding is complete, add Creator role to user
        if (creator != null && creator.IsActive)
        {
            // Ensure the Creator role exists
            if (!await _roleManager.RoleExistsAsync(Roles.Creator))
            {
                await _roleManager.CreateAsync(new IdentityRole<int> { Name = Roles.Creator, NormalizedName = Roles.Creator.ToUpper() });
            }

            // Add Creator role if user doesn't already have it
            if (!await _userManager.IsInRoleAsync(user, Roles.Creator))
            {
                await _userManager.AddToRoleAsync(user, Roles.Creator);
                _logger.LogInformation("Added Creator role to user {UserId}", user.Id);
            }
        }

        _logger.LogInformation("Completed creator onboarding for user {UserId}, IsActive: {IsActive}", user.Id, creator?.IsActive);

        return Ok(new CompleteOnboardingResponse
        {
            Success = true,
            IsActive = creator?.IsActive ?? false,
            PaymentsReceivable = creator?.PaymentsReceivable ?? false,
            PrimaryEmailConfirmed = creator?.PrimaryEmailConfirmed ?? false
        });
    }

    /// <summary>
    /// Updates the creator's profile information.
    /// </summary>
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        if (creator == null)
        {
            return NotFound("Creator record not found.");
        }

        await _creatorService.UpdateCreatorProfileAsync(creator.Id, request.DisplayName, request.Bio);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Gets all creators (admin only).
    /// </summary>
    [HttpGet("all")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllCreators()
    {
        var creators = await _creatorService.GetAllCreatorsAsync();

        return Ok(creators.Select(s => new CreatorListItem
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
    /// Activates a creator account (admin only).
    /// </summary>
    [HttpPost("{creatorId}/activate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ActivateCreator(int creatorId)
    {
        try
        {
            var creator = await _creatorService.ActivateCreatorAsync(creatorId);
            
            // Add Creator role to the user
            var user = await _userManager.FindByIdAsync(creator.UserId.ToString());
            if (user != null && !await _userManager.IsInRoleAsync(user, Roles.Creator))
            {
                await _userManager.AddToRoleAsync(user, Roles.Creator);
            }

            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Deactivates a creator account (admin only).
    /// </summary>
    [HttpPost("{creatorId}/deactivate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeactivateCreator(int creatorId)
    {
        try
        {
            var creator = await _creatorService.DeactivateCreatorAsync(creatorId);

            // Remove Creator role from the user
            var user = await _userManager.FindByIdAsync(creator.UserId.ToString());
            if (user != null && await _userManager.IsInRoleAsync(user, Roles.Creator))
            {
                await _userManager.RemoveFromRoleAsync(user, Roles.Creator);
            }

            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Updates a creator's commission rate (admin only).
    /// </summary>
    [HttpPut("{creatorId}/commission")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateCommissionRate(int creatorId, [FromBody] UpdateCommissionRequest request)
    {
        if (request.CommissionRate < 0 || request.CommissionRate > 1)
        {
            return BadRequest("Commission rate must be between 0 and 1 (0% to 100%).");
        }

        // TODO: Implement commission rate update in CreatorService
        return Ok(new { success = true });
    }

    /// <summary>
    /// Allows a user to stop being a creator. This will remove all their music from Azure storage
    /// and mark it as inactive in the database.
    /// </summary>
    [HttpPost("stop-selling")]
    public async Task<IActionResult> StopSelling()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var success = await _creatorService.StopBeingCreatorAsync(user.Id);
        if (!success)
        {
            return BadRequest("You are not currently a creator or there was an error processing your request.");
        }

        _logger.LogInformation("User {UserId} stopped being a creator", user.Id);
        return Ok(new { success = true, message = "You are no longer a creator. All your music has been removed." });
    }

    /// <summary>
    /// Deletes a specific song (marks it inactive and removes from Azure storage).
    /// </summary>
    [HttpDelete("songs/{songMetadataId}")]
    public async Task<IActionResult> DeleteSong(int songMetadataId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        if (creator == null || !creator.IsActive)
        {
            return BadRequest("You are not an active creator.");
        }

        var success = await _creatorService.DeleteCreatorSongAsync(songMetadataId, creator.Id);
        if (!success)
        {
            return BadRequest("Song not found or you do not have permission to delete it.");
        }

        _logger.LogInformation("Creator {CreatorId} deleted song {SongMetadataId}", creator.Id, songMetadataId);
        return Ok(new { success = true, message = "Song has been deleted." });
    }

    /// <summary>
    /// Gets all songs owned by the current creator.
    /// </summary>
    [HttpGet("songs")]
    public async Task<IActionResult> GetMySongs()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        if (creator == null)
        {
            return Ok(new List<object>());
        }

        var songs = await _creatorService.GetCreatorSongsAsync(creator.Id);
        return Ok(songs.Select(s => new CreatorSongItem
        {
            Id = s.Id,
            Mp3BlobPath = s.Mp3BlobPath,
            ImageBlobPath = s.ImageBlobPath,
            AlbumName = s.AlbumName,
            IsAlbumCover = s.IsAlbumCover,
            Genre = s.Genre,
            TrackNumber = s.TrackNumber,
            TrackLength = s.TrackLength,
            NumberOfStreams = s.NumberOfStreams,
            CreatedAt = s.CreatedAt
        }));
    }
}

#region Request/Response Models

public class CreatorStatusResponse
{
    public bool IsCreator { get; set; }
    public int? CreatorId { get; set; }
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

public class CreatorListItem
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

public class CreatorSongItem
{
    public int Id { get; set; }
    public string? Mp3BlobPath { get; set; }
    public string? ImageBlobPath { get; set; }
    public string? AlbumName { get; set; }
    public bool IsAlbumCover { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    public double? TrackLength { get; set; }
    public int NumberOfStreams { get; set; }
    public DateTime CreatedAt { get; set; }
}

#endregion
