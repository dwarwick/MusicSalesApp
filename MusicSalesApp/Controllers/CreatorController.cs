#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
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
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly ILogger<CreatorController> _logger;
    private readonly ITaxBanditsService _taxBanditsService;
    private readonly IConfiguration _configuration;

    public CreatorController(
        ICreatorService creatorService,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        ILogger<CreatorController> logger,
        ITaxBanditsService taxBanditsService,
        IConfiguration configuration)
    {
        _creatorService = creatorService;
        _userManager = userManager;
        _roleManager = roleManager;
        _logger = logger;
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
            OnboardedAt = creator.OnboardedAt,
            ReferralUrl = null // PayPal business account onboarding has been removed
        });
    }

    /// <summary>
    /// Starts the creator onboarding process. Now simplified to just require PayPal email and affirmation.
    /// No longer requires PayPal business account onboarding.
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

        // Validate that the user has affirmed they have a valid PayPal account
        if (!request.PayPalAccountAffirmed)
        {
            return BadRequest("You must affirm that you have a valid PayPal account in good standing to receive payments for streams.");
        }

        // Validate location certification
        if (request.LocationCertification == CreatorLocationCertification.None)
        {
            return BadRequest("You must select a creator location and tax certification option.");
        }

        // Validate acknowledgment
        if (!request.AcknowledgmentAccepted)
        {
            return BadRequest("You must accept the acknowledgment to proceed.");
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

        // Store attestation data
        await _creatorService.UpdateLocationCertificationAsync(
            creator.Id, 
            request.LocationCertification, 
            request.AcknowledgmentAccepted);

        // Handle ineligible case: non-U.S. person performing activities in the U.S.
        if (request.LocationCertification == CreatorLocationCertification.NonUSPersonInsideUS)
        {
            await _creatorService.UpdateOnboardingStatusAsync(creator.Id, CreatorOnboardingStatus.Ineligible);
            _logger.LogInformation("Creator {CreatorId} for user {UserId} marked as Ineligible - non-U.S. person performing activities in the U.S.", 
                creator.Id, user.Id);
            return Ok(new StartOnboardingResponse
            {
                Success = true,
                IsActive = false,
                TaxFormPending = false,
                IsIneligible = true
            });
        }

        // Update creator with PayPal email, affirmation, and set OnboardingStatus to Completed.
        // Uses the service method to ensure an atomic, logged state transition.
        // Previously this was done via direct DbContext manipulation which could silently fail
        // for returning creators whose OnboardingStatus was Suspended.
        await _creatorService.ResetCreatorOnboardingAsync(creator.Id, request.PayPalEmail, request.PayPalAccountAffirmed);

        // Store the PayeeRef (email) used for the W-9/W-8 request.
        // The creator will complete the tax form via the embedded Drop-in UI on the /submittaxform page,
        // and the TaxBandits webhook will set the creator status to Complete.
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            await _creatorService.UpdateTaxBanditsPayeeRefAsync(creator.Id, user.Email);
        }

        // Set tax form status to Pending — the user will be redirected to the embedded form page
        await _creatorService.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Pending);

        // Reload to get the latest state including tax form status.
        // The creator will NOT be activated here — activation happens only when the
        // TaxBandits webhook confirms the tax form is complete.
        var updatedCreator = await _creatorService.GetCreatorByUserIdAsync(user.Id);

        _logger.LogInformation("Started creator onboarding for user {UserId}, PayPal email: {PayPalEmail}, PayPal affirmed: {PayPalAffirmed}", 
            user.Id, request.PayPalEmail, request.PayPalAccountAffirmed);

        return Ok(new StartOnboardingResponse
        {
            Success = true,
            IsActive = updatedCreator?.IsActive ?? false,
            TaxFormPending = updatedCreator?.TaxFormStatus == TaxFormStatus.Pending
        });
    }

    /// <summary>
    /// Completes the creator onboarding. This is now a simplified check that returns the current status.
    /// The PayPal business account flow has been removed - we just need tax form completion.
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

        // Check if the creator has completed PayPal affirmation
        if (!creator.PayPalAccountAffirmed)
        {
            return BadRequest("Please complete the creator signup process first.");
        }

        // Check if both PayPal affirmation and tax form are complete
        if (creator.OnboardingStatus == CreatorOnboardingStatus.Completed && 
            creator.TaxFormStatus == TaxFormStatus.Completed)
        {
            // Activate the creator if not already active
            if (!creator.IsActive)
            {
                await _creatorService.ActivateCreatorAsync(creator.Id);
                creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
            }

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

        _logger.LogInformation("Checked creator onboarding status for user {UserId}, IsActive: {IsActive}", user.Id, creator?.IsActive);

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
    /// Gets a transient token and configuration for the TaxBandits Drop-in UI embedded tax form.
    /// The token is valid for 15 minutes and used to load the W-9/W-8 form on the /submittaxform page.
    /// </summary>
    [HttpGet("tax-form-token")]
    public async Task<IActionResult> GetTaxFormToken()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        if (creator == null)
        {
            return BadRequest("Creator record not found. Please start the onboarding process first.");
        }

        // Check if tax form is already completed
        if (creator.TaxFormStatus == TaxFormStatus.Completed)
        {
            return BadRequest("Your tax form has already been completed.");
        }

        // Get the PayeeRef (email) used in the original request
        var payeeRef = creator.TaxBanditsPayeeRef ?? user.Email;
        if (string.IsNullOrWhiteSpace(payeeRef))
        {
            return BadRequest("No email address found for tax form request.");
        }

        try
        {
            // Get allowed origins from Fido2:Origins configuration
            var origins = _configuration.GetSection("Fido2:Origins").Get<List<string>>() ?? new List<string>();
            if (origins.Count == 0)
            {
                _logger.LogError("No origins configured in Fido2:Origins for TaxBandits Drop-in UI");
                return StatusCode(500, new TaxFormTokenResponse
                {
                    Success = false,
                    ErrorMessage = "Server configuration error: no allowed origins configured."
                });
            }

            var tokenResult = await _taxBanditsService.GetTransientTokenAsync(origins);

            if (!tokenResult.Success)
            {
                _logger.LogError("Failed to get transient token for user {UserId}: {Error}", user.Id, tokenResult.ErrorMessage);
                return StatusCode(500, new TaxFormTokenResponse
                {
                    Success = false,
                    ErrorMessage = $"Failed to initialize tax form: {tokenResult.ErrorMessage}"
                });
            }

            var useSandbox = _configuration.GetValue<bool>("TaxBandits:UseSandbox", true);
            var businessId = _configuration["TaxBandits:BusinessId"];

            _logger.LogInformation("Tax form token generated for user {UserId}. BusinessId: {BusinessId}, UseSandbox: {UseSandbox}, TokenLength: {TokenLength}",
                user.Id, businessId, useSandbox, tokenResult.TransientToken?.Length ?? 0);

            return Ok(new TaxFormTokenResponse
            {
                Success = true,
                TransientToken = tokenResult.TransientToken,
                PayeeRef = payeeRef,
                BusinessId = businessId,
                UseSandbox = useSandbox
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while getting tax form token for user {UserId}", user.Id);
            return StatusCode(500, new TaxFormTokenResponse
            {
                Success = false,
                ErrorMessage = "An error occurred while preparing the tax form."
            });
        }
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
    public DateTime? OnboardedAt { get; set; }
    public string? ReferralUrl { get; set; }
}

public class StartOnboardingRequest
{
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? PayPalEmail { get; set; }
    /// <summary>
    /// Whether the user affirms they have a valid PayPal account in good standing.
    /// </summary>
    public bool PayPalAccountAffirmed { get; set; }
    /// <summary>
    /// The creator's location certification selection for tax eligibility purposes.
    /// </summary>
    public CreatorLocationCertification LocationCertification { get; set; }
    /// <summary>
    /// Whether the creator has accepted the acknowledgment checkbox.
    /// </summary>
    public bool AcknowledgmentAccepted { get; set; }
}

public class StartOnboardingResponse
{
    public bool Success { get; set; }
    public bool IsActive { get; set; }
    public bool TaxFormPending { get; set; }
    public bool IsIneligible { get; set; }
}

public class CompleteOnboardingRequest
{
    /// <summary>
    /// PayPal Merchant ID. This field is no longer used since the PayPal business account
    /// onboarding flow has been removed. Kept for API backward compatibility.
    /// </summary>
    [Obsolete("MerchantId is no longer used. PayPal business account onboarding has been removed.")]
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
    public DateTime CreatedAt { get; set; }
    public DateTime? OnboardedAt { get; set; }
}

public class TaxFormTokenResponse
{
    public bool Success { get; set; }
    public string? TransientToken { get; set; }
    public string? PayeeRef { get; set; }
    public string? BusinessId { get; set; }
    public bool UseSandbox { get; set; }
    public string? ErrorMessage { get; set; }
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
