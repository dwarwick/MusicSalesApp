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
    private readonly ILogger<CreatorController> _logger;
    private readonly ITaxBanditsService _taxBanditsService;
    private readonly IConfiguration _configuration;

    public CreatorController(
        ICreatorService creatorService,
        UserManager<ApplicationUser> userManager,
        ILogger<CreatorController> logger,
        ITaxBanditsService taxBanditsService,
        IConfiguration configuration)
    {
        _creatorService = creatorService;
        _userManager = userManager;
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
                HasCreatorRecord = false,
                OnboardingStatus = null,
                IsActive = false
            });
        }

        return Ok(new CreatorStatusResponse
        {
            HasCreatorRecord = true,
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
    /// Starts the creator onboarding process.
    /// PayPal confirmation and tax form completion are optional at signup, but required before payouts.
    /// </summary>
    [HttpPost("start-onboarding")]
    public async Task<IActionResult> StartOnboarding([FromBody] StartOnboardingRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var result = await _creatorService.StartOnboardingAsync(new CreatorOnboardingInput
        {
            UserId = user.Id,
            UserEmail = user.Email,
            DisplayName = request.DisplayName,
            Bio = request.Bio,
            PayPalEmail = request.PayPalEmail,
            PayPalAccountAffirmed = request.PayPalAccountAffirmed,
            CreatorAgreementAccepted = request.CreatorAgreementAccepted,
            LocationCertification = request.LocationCertification,
            AcknowledgmentAccepted = request.AcknowledgmentAccepted,
            PayoutRequirementsAcknowledged = request.PayoutRequirementsAcknowledged,
            SubmitTaxFormNow = request.SubmitTaxFormNow
        });

        if (!result.Success)
        {
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new StartOnboardingResponse
        {
            Success = result.Success,
            IsActive = result.IsActive,
            TaxFormPending = result.TaxFormPending,
            IsIneligible = result.IsIneligible
        });
    }

    /// <summary>
    /// Completes the creator onboarding. This is now a simplified check that returns the current status.
    /// PayPal and tax form completion are payout requirements, not creator activation requirements.
    /// </summary>
    [HttpPost("complete-onboarding")]
    public async Task<IActionResult> CompleteOnboarding([FromBody] CompleteOnboardingRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var result = await _creatorService.CompleteOnboardingAsync(user.Id);
        if (!result.Success)
        {
            return BadRequest(result.ErrorMessage);
        }

        _logger.LogInformation("Checked creator onboarding status for user {UserId}, IsActive: {IsActive}", user.Id, result.IsActive);

        return Ok(new CompleteOnboardingResponse
        {
            Success = result.Success,
            IsActive = result.IsActive,
            PaymentsReceivable = result.PaymentsReceivable,
            PrimaryEmailConfirmed = result.PrimaryEmailConfirmed
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
    /// Initiates a tax form update for an active creator who wants to submit a new W8/W9
    /// (e.g., because their address has changed).
    /// </summary>
    [HttpPost("initiate-tax-form-update")]
    public async Task<IActionResult> InitiateTaxFormUpdate()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        if (creator == null || !creator.IsActive)
        {
            return BadRequest("You must be an active creator to update your tax form.");
        }

        // Set tax form status to Pending so the embedded form page can load
        await _creatorService.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Pending);

        // Update the PayeeRef (email) for the new request
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            await _creatorService.UpdateTaxBanditsPayeeRefAsync(creator.Id, user.Email);
        }

        _logger.LogInformation("Creator {CreatorId} initiated tax form update for user {UserId}", creator.Id, user.Id);

        return Ok(new { success = true });
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

        // Tax form token is only available when status is Pending
        if (creator.TaxFormStatus != TaxFormStatus.Pending)
        {
            return BadRequest("No pending tax form request. Please initiate a tax form submission first.");
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
            // Supports: 
            //   1. JSON array in appsettings.json: "Origins": ["https://example.com", "https://www.example.com"]
            //   2. Indexed environment variables: Fido2:Origins:0, Fido2:Origins:1
            //   3. Comma-separated string fallback: Fido2:Origins = "https://example.com,https://www.example.com"
            var origins = _configuration.GetSection("Fido2:Origins").Get<List<string>>() ?? new List<string>();
            
            // Fallback: check if origins is empty but a comma-separated string was provided
            if (origins.Count == 0)
            {
                var originsString = _configuration["Fido2:Origins"];
                if (!string.IsNullOrWhiteSpace(originsString))
                {
                    origins = originsString
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    _logger.LogInformation("Parsed {Count} origins from comma-separated string", origins.Count);
                }
            }
            
            _logger.LogInformation("Configured origins for TaxBandits: {Origins}", string.Join(", ", origins));
            
            if (origins.Count == 0)
            {
                _logger.LogError("No origins configured in Fido2:Origins for TaxBandits Drop-in UI. " +
                    "Set as indexed env vars (Fido2:Origins:0, Fido2:Origins:1) or comma-separated (Fido2:Origins=url1,url2)");
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

            var businessId = _configuration["TaxBandits:BusinessId"];
            var scriptUrl = _configuration["TaxBandits:ScriptUrl"];

            _logger.LogInformation("Tax form token generated for user {UserId}. BusinessId: {BusinessId}, TokenLength: {TokenLength}",
                user.Id, businessId, tokenResult.TransientToken?.Length ?? 0);

            return Ok(new TaxFormTokenResponse
            {
                Success = true,
                TransientToken = tokenResult.TransientToken,
                PayeeRef = payeeRef,
                BusinessId = businessId,
                ScriptUrl = scriptUrl
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
    public bool HasCreatorRecord { get; set; }
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
    /// Whether the user affirms they own or are authorized to use a valid PayPal account in good standing.
    /// </summary>
    public bool PayPalAccountAffirmed { get; set; }
    /// <summary>
    /// Whether the creator accepted the Creator Agreement required for activation.
    /// </summary>
    public bool CreatorAgreementAccepted { get; set; }
    /// <summary>
    /// The creator's location certification selection for tax eligibility purposes.
    /// Kept for API compatibility; not required for creator activation.
    /// </summary>
    public CreatorLocationCertification LocationCertification { get; set; }
    /// <summary>
    /// Whether the creator has accepted the acknowledgment checkbox.
    /// Kept for API compatibility; not required for creator activation.
    /// </summary>
    public bool AcknowledgmentAccepted { get; set; }
    /// <summary>
    /// Whether the creator acknowledges PayPal and tax form completion are required before payouts.
    /// Kept for API compatibility; not required for creator activation.
    /// </summary>
    public bool PayoutRequirementsAcknowledged { get; set; }
    /// <summary>
    /// Whether the creator wants to complete the W-9/W-8 tax form immediately after signup.
    /// </summary>
    public bool SubmitTaxFormNow { get; set; }
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
    public string? ScriptUrl { get; set; }
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
