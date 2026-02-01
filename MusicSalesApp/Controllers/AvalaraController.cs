#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Text.Json;

namespace MusicSalesApp.Controllers;

/// <summary>
/// API controller for handling Avalara/Track1099 webhook notifications.
/// Handles W-9/W-8 tax form completion events from the Avalara embedded form flow.
/// </summary>
[Route("api/avalara")]
[ApiController]
[AllowAnonymous] // Webhooks come from Avalara, not authenticated users
public class AvalaraController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly ICreatorService _creatorService;
    private readonly ICreatorEmailService _creatorEmailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AvalaraController> _logger;
    private readonly IHubContext<WebhookStatusHub> _hubContext;

    // TIN Match status values from Avalara/Track1099
    private const string TinMatchStatusMatched = "matched";
    private const string TinMatchStatusRejected = "rejected";
    private const string TinMatchStatusPending = "pending";
    private const string TinMatchStatusUnknown = "unknown";

    // Backup withholding rate for US creators (24% per IRS regulations)
    private const decimal BackupWithholdingRate = 0.24m;

    public AvalaraController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        ICreatorService creatorService,
        ICreatorEmailService creatorEmailService,
        IConfiguration configuration,
        ILogger<AvalaraController> logger,
        IHubContext<WebhookStatusHub> hubContext)
    {
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
        _roleManager = roleManager;
        _creatorService = creatorService;
        _creatorEmailService = creatorEmailService;
        _configuration = configuration;
        _logger = logger;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Handles incoming Avalara/Track1099 webhook notifications for W-9 form completion.
    /// This endpoint is called when a user signs their W-9 form via the embedded Avalara form.
    /// </summary>
    [HttpPost("w9-complete")]
    public async Task<IActionResult> HandleW9Complete()
    {
        try
        {
            // Enable buffering so the body can be read multiple times if needed
            Request.EnableBuffering();

            // Read the raw body
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();

            // Reset the stream position for any subsequent reads
            Request.Body.Position = 0;

            _logger.LogInformation("Received Avalara W9 complete webhook: {Body}", body);

            // Parse the webhook event
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Extract the data object
            if (!root.TryGetProperty("data", out var data))
            {
                _logger.LogWarning("Avalara webhook missing data property");
                return BadRequest("Missing data property");
            }

            // Extract the id (W9 submission ID)
            string? submissionId = null;
            if (data.TryGetProperty("id", out var idElement))
            {
                submissionId = idElement.GetString();
            }

            // Extract attributes
            if (!data.TryGetProperty("attributes", out var attributes))
            {
                _logger.LogWarning("Avalara webhook missing attributes property");
                return BadRequest("Missing attributes property");
            }

            // Extract key fields from attributes
            string? referenceId = null;
            string? tinMatchStatus = null;
            string? formType = null;
            string? signedAt = null;

            if (attributes.TryGetProperty("reference_id", out var refIdElement))
            {
                referenceId = refIdElement.GetString(); // This is the user's email
            }

            if (attributes.TryGetProperty("tin_match_status", out var tinStatusElement))
            {
                tinMatchStatus = tinStatusElement.GetString();
            }

            if (attributes.TryGetProperty("form_type", out var formTypeElement))
            {
                formType = formTypeElement.GetString();
            }

            if (attributes.TryGetProperty("signed_at", out var signedAtElement))
            {
                signedAt = signedAtElement.GetString();
            }

            _logger.LogInformation(
                "Processing Avalara W9 webhook: SubmissionId={SubmissionId}, ReferenceId={ReferenceId}, TinMatchStatus={TinMatchStatus}, FormType={FormType}, SignedAt={SignedAt}",
                submissionId, referenceId, tinMatchStatus, formType, signedAt);

            // Validate we have the minimum required fields
            if (string.IsNullOrWhiteSpace(referenceId))
            {
                _logger.LogWarning("Avalara webhook missing reference_id");
                return BadRequest("Missing reference_id");
            }

            // The form must be signed to proceed
            if (string.IsNullOrWhiteSpace(signedAt))
            {
                _logger.LogInformation("Avalara webhook received but form not yet signed. ReferenceId={ReferenceId}", referenceId);
                return Ok(new { status = "not_signed" });
            }

            var baseUrl = GetBaseUrl();

            // Find the user by email (reference_id)
            var user = await _userManager.FindByEmailAsync(referenceId);
            if (user == null)
            {
                _logger.LogWarning("Could not find user for Avalara webhook. ReferenceId={ReferenceId}", referenceId);
                return Ok(new { status = "user_not_found" });
            }

            // Get the creator record
            var creator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
            if (creator == null)
            {
                _logger.LogWarning("Could not find creator record for user {UserId}", user.Id);
                return Ok(new { status = "creator_not_found" });
            }

            // Parse submission ID as Guid if possible
            Guid? submissionGuid = null;
            if (Guid.TryParse(submissionId, out var parsedGuid))
            {
                submissionGuid = parsedGuid;
            }

            // Store the W9 submission ID
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var creatorToUpdate = await context.Creators.FindAsync(creator.Id);
            if (creatorToUpdate != null)
            {
                creatorToUpdate.W9SubmissionId = submissionGuid;
                creatorToUpdate.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }

            // Handle based on tin_match_status
            var normalizedStatus = tinMatchStatus?.ToLowerInvariant() ?? TinMatchStatusUnknown;

            switch (normalizedStatus)
            {
                case TinMatchStatusMatched:
                    await HandleTinMatchedAsync(user, creator, submissionGuid, formType, baseUrl);
                    break;

                case TinMatchStatusRejected:
                    await HandleTinRejectedAsync(user, creator, referenceId, baseUrl);
                    break;

                case TinMatchStatusPending:
                case TinMatchStatusUnknown:
                    await HandleTinPendingAsync(user, creator, referenceId, submissionId, baseUrl);
                    break;

                default:
                    _logger.LogWarning("Unknown tin_match_status: {Status}", tinMatchStatus);
                    await HandleTinPendingAsync(user, creator, referenceId, submissionId, baseUrl);
                    break;
            }

            return Ok(new { status = "success" });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Avalara webhook JSON");
            return BadRequest("Invalid JSON");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Avalara webhook");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Handles the case when TIN match status is "matched".
    /// Sets the user's role to Creator, sets IsActive to true, and OnBoardedAt to current UTC time.
    /// </summary>
    private async Task HandleTinMatchedAsync(ApplicationUser user, Creator creator, Guid? submissionGuid, string? formType, string baseUrl)
    {
        _logger.LogInformation("TIN match successful for user {UserId}. Proceeding with creator onboarding.", user.Id);

        // Determine if this is a W-9 (US) or W-8 (foreign) form
        var isW9 = string.Equals(formType, "W-9", StringComparison.OrdinalIgnoreCase);
        var taxResidencyType = isW9 ? TaxResidencyType.US : TaxResidencyType.Foreign;
        var taxResidencyCountry = isW9 ? "US" : null;

        // For W-9, withholding rate is 0% unless subject to backup withholding
        // (We don't have backup withholding info from Avalara, so default to 0%)
        var withholdingRate = isW9 ? 0m : 0.30m; // Default 30% for foreign

        // Complete the creator onboarding with tax data
        await _creatorService.UpdateTaxFormStatusWithTaxDataAsync(
            creator.Id,
            TaxFormStatus.Completed,
            taxResidencyType,
            taxResidencyCountry,
            treatyCountry: null,
            claimedTreatyArticle: null,
            withholdingRate: withholdingRate,
            taxFormExpirationDate: null, // W-9 forms don't expire
            w9SubmissionId: submissionGuid,
            subjectToBackupWithholding: false);

        // Reload creator to get updated status
        var updatedCreator = await _creatorService.GetCreatorByUserIdAsync(user.Id);
        if (updatedCreator == null)
        {
            _logger.LogWarning("Creator record not found after update for user {UserId}", user.Id);
            return;
        }

        // Check if PayPal onboarding is also complete
        if (updatedCreator.OnboardingStatus == CreatorOnboardingStatus.Completed)
        {
            // Both onboarding processes are complete - add Creator role
            _logger.LogInformation("Both PayPal and tax form onboarding complete for user {UserId}. Adding Creator role.", user.Id);

            // Ensure the Creator role exists
            var normalizedRoleName = _roleManager.NormalizeKey(Common.Helpers.Roles.Creator);
            if (!await _roleManager.RoleExistsAsync(Common.Helpers.Roles.Creator))
            {
                await _roleManager.CreateAsync(new IdentityRole<int>
                {
                    Name = Common.Helpers.Roles.Creator,
                    NormalizedName = normalizedRoleName
                });
            }

            // Add Creator role if user doesn't already have it
            if (!await _userManager.IsInRoleAsync(user, Common.Helpers.Roles.Creator))
            {
                await _userManager.AddToRoleAsync(user, Common.Helpers.Roles.Creator);
                _logger.LogInformation("Added Creator role to user {UserId}", user.Id);
            }

            // Activate the creator and set OnboardedAt
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var creatorToActivate = await context.Creators.FindAsync(updatedCreator.Id);
            if (creatorToActivate != null)
            {
                creatorToActivate.IsActive = true;
                creatorToActivate.OnboardedAt = DateTime.UtcNow;
                creatorToActivate.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }

            _logger.LogInformation("Activated creator {CreatorId} for user {UserId}", updatedCreator.Id, user.Id);
        }
        else
        {
            _logger.LogInformation(
                "User {UserId} completed tax form but PayPal onboarding is not complete. PayPal Status: {PayPalStatus}",
                user.Id, updatedCreator.OnboardingStatus);
        }

        // Broadcast SignalR update
        await BroadcastWebhookStatusAsync(
            user.Id,
            "TaxFormCompleted",
            true,
            "Your tax form has been verified successfully!",
            "Completed");

        // Send success email
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            await _creatorEmailService.SendTaxFormSuccessEmailAsync(user.Email, baseUrl, isW9 ? "W-9" : "W-8");
        }
    }

    /// <summary>
    /// Handles the case when TIN match status is "rejected".
    /// Sends an email advising the user to enter their legal name and correct SSN/EIN.
    /// </summary>
    private async Task HandleTinRejectedAsync(ApplicationUser user, Creator creator, string userEmail, string baseUrl)
    {
        _logger.LogWarning("TIN match rejected for user {UserId}.", user.Id);

        // Update tax form status to Failed
        await _creatorService.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.Failed);

        // Broadcast SignalR update
        await BroadcastWebhookStatusAsync(
            user.Id,
            "TaxFormStatus",
            false,
            "TIN verification failed. Please check your information and try again.",
            "Failed");

        // Send rejection email
        await _creatorEmailService.SendTinRejectedEmailAsync(userEmail, baseUrl);
    }

    /// <summary>
    /// Handles the case when TIN match status is "pending" or "unknown".
    /// Notifies the user of a delay and sends an email to admin.
    /// </summary>
    private async Task HandleTinPendingAsync(ApplicationUser user, Creator creator, string userEmail, string? submissionId, string baseUrl)
    {
        _logger.LogInformation("TIN match pending for user {UserId}. SubmissionId={SubmissionId}", user.Id, submissionId);

        // Update tax form status to TinMatchInProgress
        await _creatorService.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.TinMatchInProgress);

        // Broadcast SignalR update
        await BroadcastWebhookStatusAsync(
            user.Id,
            "TaxFormStatus",
            true,
            "Your tax form is being verified. This process may take up to 24 hours. We will notify you once verification is complete.",
            "TinMatchInProgress");

        // Send pending notification emails (to user and admin)
        await _creatorEmailService.SendTinPendingEmailAsync(userEmail, submissionId, baseUrl);
    }

    /// <summary>
    /// Broadcasts a webhook status update via SignalR to notify the user's browser.
    /// </summary>
    private async Task BroadcastWebhookStatusAsync(int userId, string webhookType, bool isSuccess, string message, string? newStatus)
    {
        try
        {
            var statusMessage = new WebhookStatusMessage
            {
                UserId = userId,
                WebhookType = webhookType,
                IsSuccess = isSuccess,
                Message = message,
                NewStatus = newStatus
            };

            // Broadcast to all connected clients - the client will filter by UserId
            await _hubContext.Clients.All.SendAsync("ReceiveWebhookStatus", statusMessage);

            _logger.LogInformation(
                "Broadcasted webhook status via SignalR: UserId={UserId}, Type={WebhookType}, Success={IsSuccess}",
                userId, webhookType, isSuccess);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting webhook status via SignalR for user {UserId}", userId);
            // Don't throw - webhook processing should continue even if SignalR broadcast fails
        }
    }

    /// <summary>
    /// Gets the base URL for constructing email links.
    /// </summary>
    private string GetBaseUrl()
    {
        var scheme = Request.Scheme;
        var host = Request.Host.Value;
        return $"{scheme}://{host}";
    }
}
