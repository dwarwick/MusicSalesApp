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
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly ILogger<CreatorController> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ITaxBanditsService _taxBanditsService;
    private readonly IAvalaraTaxService _avalaraTaxService;
    private readonly IConfiguration _configuration;

    public CreatorController(
        ICreatorService creatorService,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        ILogger<CreatorController> logger,
        IDbContextFactory<AppDbContext> dbContextFactory,
        ITaxBanditsService taxBanditsService,
        IAvalaraTaxService avalaraTaxService,
        IConfiguration configuration)
    {
        _creatorService = creatorService;
        _userManager = userManager;
        _roleManager = roleManager;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _taxBanditsService = taxBanditsService;
        _avalaraTaxService = avalaraTaxService;
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
            return BadRequest("You must affirm that you have a valid PayPal account in good standing to receive royalty payments.");
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

        // Update creator with PayPal email and affirmation
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var creatorToUpdate = await context.Creators.FindAsync(creator.Id);
        if (creatorToUpdate != null)
        {
            creatorToUpdate.PayPalEmail = request.PayPalEmail;
            creatorToUpdate.PayPalAccountAffirmed = request.PayPalAccountAffirmed;
            creatorToUpdate.OnboardingStatus = CreatorOnboardingStatus.Completed;
            // PaymentsReceivable and PrimaryEmailConfirmed are set to true based on user affirmation
            // rather than PayPal verification. The business account onboarding flow has been removed.
            // These fields are kept for backward compatibility with existing queries.
            creatorToUpdate.PaymentsReceivable = true;
            creatorToUpdate.PrimaryEmailConfirmed = true;
            creatorToUpdate.OnboardedAt = DateTime.UtcNow;
            creatorToUpdate.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // Tax form collection is now handled via the Avalara embedded form flow.
        // Users will click the W-9 or W-8BEN button in the UI to open the form directly,
        // rather than receiving an email from TaxBandits.
        // The TaxBandits service is no longer called during onboarding.
        
        // Set initial tax form status to NotStarted - user will complete it via Avalara UI
        await _creatorService.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.NotStarted);

        // Reload to get the latest state including tax form status
        var updatedCreator = await _creatorService.GetCreatorByUserIdAsync(user.Id);

        // Check if both PayPal affirmation and tax form are complete to activate
        if (updatedCreator != null && updatedCreator.TaxFormStatus == TaxFormStatus.Completed)
        {
            // Activate the creator
            await _creatorService.ActivateCreatorAsync(updatedCreator.Id);
            
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

            updatedCreator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        }

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
    /// Resends the W-9/W-8 tax form email by deleting the existing incomplete form and requesting a new one.
    /// This is used when a user abandons the initial form and needs a new email invitation.
    /// </summary>
    [HttpPost("resend-tax-form")]
    public async Task<IActionResult> ResendTaxForm()
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

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        try
        {
            // First, delete the incomplete W-9/W-8 form
            _logger.LogInformation("Deleting incomplete W-9/W-8 for user {UserId}, PayeeRef {PayeeRef}", user.Id, payeeRef);
            var deleteResult = await _taxBanditsService.DeleteW9Async(payeeRef);

            if (!deleteResult.Success)
            {
                // Business Logic: We continue with the resend even if delete fails because:
                // 1. The form might not exist in TaxBandits (user never started it)
                // 2. The form was already deleted previously
                // 3. TaxBandits had a transient error
                // In all cases, requesting a new form is the desired outcome for the user.
                _logger.LogWarning("W-9/W-8 delete returned error for user {UserId}: {Error}. Proceeding with resend anyway.", 
                    user.Id, deleteResult.ErrorMessage);
            }
            else
            {
                _logger.LogInformation("Successfully deleted incomplete W-9/W-8 for user {UserId}", user.Id);
            }

            // Now request a new W-9/W-8 form
            var w9Result = await _taxBanditsService.RequestW9ByEmailAsync(user.Id, payeeRef, baseUrl);

            if (w9Result.Success)
            {
                _logger.LogInformation("W-9/W-8 resend successful for user {UserId}", user.Id);

                // Update the creator's tax form status to Pending
                await _creatorService.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Pending);

                return Ok(new ResendTaxFormResponse
                {
                    Success = true,
                    Message = "A new tax form email has been sent. Please check your inbox."
                });
            }
            else
            {
                _logger.LogError("W-9/W-8 resend failed for user {UserId}: {Error}", user.Id, w9Result.ErrorMessage);
                return StatusCode(500, new ResendTaxFormResponse
                {
                    Success = false,
                    Message = $"Failed to send new tax form email: {w9Result.ErrorMessage}"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while resending W-9/W-8 for user {UserId}", user.Id);
            return StatusCode(500, new ResendTaxFormResponse
            {
                Success = false,
                Message = "An error occurred while resending the tax form email."
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

    /// <summary>
    /// Creates an Avalara form request for W-9 or W-8BEN tax forms.
    /// Returns the form request data that can be used with the Avalara JavaScript SDK
    /// to display an embedded form for the user to complete.
    /// </summary>
    /// <param name="request">The form request parameters.</param>
    [HttpPost("avalara-form-request")]
    public async Task<IActionResult> CreateAvalaraFormRequest([FromBody] AvalaraFormRequestRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return BadRequest("User must have a verified email address.");
        }

        // Validate form type
        var validFormTypes = new[] { "W-9", "W-8BEN" };
        if (string.IsNullOrWhiteSpace(request.FormType) || !validFormTypes.Contains(request.FormType))
        {
            return BadRequest($"FormType must be one of: {string.Join(", ", validFormTypes)}");
        }

        // Check if user already has a creator record and if tax form is already completed
        var existingCreator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        if (existingCreator != null && existingCreator.TaxFormStatus == TaxFormStatus.Completed)
        {
            return BadRequest("You have already completed your tax form.");
        }

        try
        {
            // Use user ID as the reference_id to correlate the form with this user
            var referenceId = user.Id.ToString();
            
            var result = await _avalaraTaxService.CreateFormRequestAsync(
                request.FormType,
                referenceId,
                request.Ttl ?? 3600);

            if (result.Success)
            {
                _logger.LogInformation("Avalara form request created for user {UserId}, FormType: {FormType}, FormRequestId: {FormRequestId}",
                    user.Id, request.FormType, result.FormRequestId);

                // Update the creator's tax form status to Pending if they have a creator record
                if (existingCreator != null)
                {
                    await _creatorService.UpdateTaxFormStatusAsync(existingCreator.Id, TaxFormStatus.Pending);
                    
                    // Store the PayeeRef (email) used for the form request
                    if (!string.IsNullOrWhiteSpace(user.Email))
                    {
                        await _creatorService.UpdateTaxBanditsPayeeRefAsync(existingCreator.Id, user.Email);
                    }
                }

                return Ok(new AvalaraFormRequestResponse
                {
                    Success = true,
                    FormRequestJson = result.FormRequestJson,
                    FormRequestId = result.FormRequestId,
                    FormType = result.FormType,
                    ExpiresAt = result.ExpiresAt
                });
            }
            else
            {
                _logger.LogError("Avalara form request failed for user {UserId}: {Error}", user.Id, result.ErrorMessage);
                return StatusCode(500, new AvalaraFormRequestResponse
                {
                    Success = false,
                    ErrorMessage = result.ErrorMessage
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while creating Avalara form request for user {UserId}", user.Id);
            return StatusCode(500, new AvalaraFormRequestResponse
            {
                Success = false,
                ErrorMessage = "An error occurred while creating the form request."
            });
        }
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
}

public class StartOnboardingResponse
{
    public bool Success { get; set; }
    public bool IsActive { get; set; }
    public bool TaxFormPending { get; set; }
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

public class ResendTaxFormResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
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

public class AvalaraFormRequestRequest
{
    /// <summary>
    /// The type of form to request: "W-9" for US taxpayers or "W-8BEN" for non-US taxpayers.
    /// </summary>
    public string? FormType { get; set; }

    /// <summary>
    /// Optional: Time to live in seconds for the form request (default: 3600, max: 86400).
    /// </summary>
    public int? Ttl { get; set; }
}

public class AvalaraFormRequestResponse
{
    /// <summary>
    /// Indicates whether the form request was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The complete JSON response from Avalara API to be passed to the JavaScript SDK.
    /// </summary>
    public string? FormRequestJson { get; set; }

    /// <summary>
    /// The unique ID of the form request.
    /// </summary>
    public string? FormRequestId { get; set; }

    /// <summary>
    /// The form type (W-9 or W-8BEN).
    /// </summary>
    public string? FormType { get; set; }

    /// <summary>
    /// When the form request expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Error message if the request failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

#endregion
