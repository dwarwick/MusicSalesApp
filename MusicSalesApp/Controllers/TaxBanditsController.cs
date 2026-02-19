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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MusicSalesApp.Controllers;

/// <summary>
/// API controller for handling TaxBandits webhook notifications.
/// Handles W-9/W-8 tax form completion events.
/// </summary>
[Route("api/taxbandits")]
[ApiController]
[AllowAnonymous] // Webhooks come from TaxBandits, not authenticated users
public class TaxBanditsController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly ICreatorService _creatorService;
    private readonly ICreatorEmailService _creatorEmailService;
    private readonly ITaxBanditsService _taxBanditsService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TaxBanditsController> _logger;
    private readonly IHubContext<WebhookStatusHub> _hubContext;

    // W-9/W-8 completion status
    private const string StatusCompleted = "COMPLETED";
    
    // W-8/W-9 failure statuses that require user notification
    private static readonly HashSet<string> FailureStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "INVALID",
        "BOUNCED",
        "ORDER_NOT_CREATED"
    };
    
    // Instant TIN Match status codes
    private const string TinStatusCodeSuccess = "TIN-001";
    private const string TinStatusCodeFailed = "TIN-002";
    private const string TinStatusCodeOnHold = "TIN-003";
    
    // Backup withholding rate for US creators (24% per IRS regulations)
    private const decimal BackupWithholdingRate = 0.24m;

    public TaxBanditsController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        ICreatorService creatorService,
        ICreatorEmailService creatorEmailService,
        ITaxBanditsService taxBanditsService,
        IConfiguration configuration,
        ILogger<TaxBanditsController> logger,
        IHubContext<WebhookStatusHub> hubContext)
    {
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
        _roleManager = roleManager;
        _creatorService = creatorService;
        _creatorEmailService = creatorEmailService;
        _taxBanditsService = taxBanditsService;
        _configuration = configuration;
        _logger = logger;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Handles incoming TaxBandits webhook notifications for W-9/W-8 form status changes.
    /// See: https://developer.taxbandits.com/docs/webhooks/whcertificatestatuschange/
    /// </summary>
    [HttpPost("w9Complete")]
    public async Task<IActionResult> HandleW9CompleteWebhook()
    {
        try
        {
            // Enable buffering so the body can be read multiple times if needed
            Request.EnableBuffering();

            // Read the raw body for verification
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();

            // Reset the stream position for any subsequent reads
            Request.Body.Position = 0;

            _logger.LogInformation("Received TaxBandits webhook: {Body}", body);

            // Verify the webhook signature
            var isValid = VerifyWebhookSignature(body);
            if (!isValid)
            {
                _logger.LogWarning("TaxBandits webhook signature verification failed");
                return Unauthorized("Invalid webhook signature");
            }

            // Parse the webhook event
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Determine the form type
            if (!root.TryGetProperty("FormType", out var formTypeElement))
            {
                _logger.LogWarning("TaxBandits webhook missing FormType");
                return BadRequest("Missing FormType");
            }

            var formType = formTypeElement.GetString();
            _logger.LogInformation("Processing TaxBandits webhook for FormType: {FormType}", formType);

            // Handle W-9 form completion (TaxBandits sends "FORMW9" as FormType)
            if (string.Equals(formType, "FORMW9", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("FormW9", out var formW9))
            {
                return await HandleFormW9WebhookAsync(formW9, body);
            }
            // Handle W-8 form completion (for non-US persons, TaxBandits sends "FORMW8BEN" as FormType)
            else if (string.Equals(formType, "FORMW8BEN", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("FormW8Ben", out var formW8))
            {
                return await HandleFormW8WebhookAsync(formW8, body);
            }
            else
            {
                _logger.LogInformation("Received unknown TaxBandits form type: {FormType}", formType);
                return Ok(new { status = "ignored", form_type = formType });
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse TaxBandits webhook JSON");
            return BadRequest("Invalid JSON");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing TaxBandits webhook");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Handles incoming TaxBandits webhook notifications for Instant TIN Matching status changes.
    /// This is called when a TIN match that was ON HOLD (TIN-003) completes.
    /// See: https://developer.taxbandits.com/docs/InstantTINMatching/Webhook
    /// </summary>
    [HttpPost("tinmatchcomplete")]
    public async Task<IActionResult> HandleTinMatchCompleteWebhook()
    {
        try
        {
            Request.EnableBuffering();

            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();

            Request.Body.Position = 0;

            _logger.LogInformation("Received TaxBandits TIN Match webhook: {Body}", body);

            // Verify the webhook signature
            var isValid = VerifyWebhookSignature(body);
            if (!isValid)
            {
                _logger.LogWarning("TaxBandits TIN Match webhook signature verification failed");
                return Unauthorized("Invalid webhook signature");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string? tinStatusCode = null;
            string? tinStatus = null;
            string? tinStatusMsg = null;
            string? recordId = null;

            if (root.TryGetProperty("TINStatusCode", out var statusCodeEl))
                tinStatusCode = statusCodeEl.GetString();
            if (root.TryGetProperty("TINStatus", out var statusEl))
                tinStatus = statusEl.GetString();
            if (root.TryGetProperty("TINStatusMsg", out var statusMsgEl))
                tinStatusMsg = statusMsgEl.GetString();
            if (root.TryGetProperty("RecordId", out var recordIdEl))
                recordId = recordIdEl.GetString();

            _logger.LogInformation(
                "Processing TIN Match webhook: RecordId={RecordId}, StatusCode={TINStatusCode}, Status={TINStatus}",
                recordId, tinStatusCode, tinStatus);

            // Find the W9Request that has TinMatchInProgress status
            // The webhook doesn't include the user email/PayeeRef directly,
            // so we look for users with TIN match in progress
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            // Get all creators with TinMatchInProgress status
            var creatorsInProgress = await GetCreatorsWithTinMatchInProgressAsync();

            if (creatorsInProgress.Count == 0)
            {
                _logger.LogWarning("No creators found with TinMatchInProgress status for TIN Match webhook. RecordId={RecordId}", recordId);
                return Ok(new { status = "no_matching_creators" });
            }

            var baseUrl = GetBaseUrl();

            // Process TIN match result for all creators with TinMatchInProgress
            // In practice, there should typically be only one at a time
            foreach (var (creator, w9Request) in creatorsInProgress)
            {
                var userEmail = w9Request?.Email;

                _logger.LogInformation(
                    "Processing TIN Match result for creator {CreatorId}, user {UserId}: StatusCode={TINStatusCode}",
                    creator.Id, creator.UserId, tinStatusCode);

                if (string.Equals(tinStatusCode, TinStatusCodeSuccess, StringComparison.OrdinalIgnoreCase))
                {
                    // TIN-001: SUCCESS
                    if (w9Request != null)
                    {
                        w9Request.IsCompleted = true;
                        w9Request.CompletedAt = DateTime.UtcNow;
                        w9Request.UpdatedAt = DateTime.UtcNow;
                    }

                    // Read backup withholding from the stored W9Request field
                    bool subjectToBackupWithholding = w9Request?.SubjectToBackupWithholding ?? false;

                    Guid? submissionGuid = null;
                    if (w9Request != null && Guid.TryParse(w9Request.SubmissionId, out var parsedGuid))
                    {
                        submissionGuid = parsedGuid;
                    }

                    var withholdingRate = subjectToBackupWithholding ? BackupWithholdingRate : 0m;

                    await CompleteCreatorOnboardingWithTaxDataAsync(
                        creator.UserId,
                        TaxResidencyType.US,
                        taxResidencyCountry: "US",
                        withholdingRate: withholdingRate,
                        taxFormExpirationDate: null,
                        taxBanditsSubmissionId: submissionGuid,
                        subjectToBackupWithholding: subjectToBackupWithholding);

                    await BroadcastWebhookStatusAsync(
                        creator.UserId,
                        "TaxFormCompleted",
                        true,
                        "Your tax form has been completed successfully!",
                        "Completed");

                    if (!string.IsNullOrWhiteSpace(userEmail))
                    {
                        await _creatorEmailService.SendTaxFormSuccessEmailAsync(userEmail, baseUrl, "W-9");
                    }
                }
                else if (string.Equals(tinStatusCode, TinStatusCodeFailed, StringComparison.OrdinalIgnoreCase))
                {
                    // TIN-002: FAILED
                    _logger.LogWarning("TIN Match failed via webhook for creator {CreatorId}, user {UserId}",
                        creator.Id, creator.UserId);

                    // Record the failure timestamp for 24-hour cooldown enforcement
                    await _creatorService.SetTinMatchFailedAsync(creator.Id);

                    if (!string.IsNullOrWhiteSpace(userEmail))
                    {
                        await _creatorEmailService.SendTaxFormFailedEmailAsync(userEmail, baseUrl, "W-9",
                            "TIN verification failed. Please double-check your Tax Identification Number and resubmit. You may retry after 24 hours.");
                    }

                    await BroadcastWebhookStatusAsync(
                        creator.UserId,
                        "TaxFormStatus",
                        false,
                        "TIN verification failed. You may retry after 24 hours.",
                        "Failed");
                }
                else
                {
                    _logger.LogWarning(
                        "Unexpected TIN Match status in webhook for creator {CreatorId}: {TINStatusCode}",
                        creator.Id, tinStatusCode);
                }
            }

            await context.SaveChangesAsync();

            return Ok(new { status = "success" });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse TaxBandits TIN Match webhook JSON");
            return BadRequest("Invalid JSON");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing TaxBandits TIN Match webhook");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Gets all creators with TinMatchInProgress status along with their most recent W9Request.
    /// </summary>
    private async Task<List<(Creator Creator, W9Request? W9Request)>> GetCreatorsWithTinMatchInProgressAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var creators = await context.Creators
            .Where(c => c.TaxFormStatus == TaxFormStatus.TinMatchInProgress)
            .ToListAsync();

        var result = new List<(Creator, W9Request?)>();

        foreach (var creator in creators)
        {
            var w9Request = await context.W9Requests
                .Where(w => w.UserId == creator.UserId)
                .OrderByDescending(w => w.CreatedAt)
                .FirstOrDefaultAsync();

            result.Add((creator, w9Request));
        }

        return result;
    }

    /// <summary>
    /// Handles W-9 form status change webhook for US persons.
    /// Sets tax residency type to US. Applies 24% backup withholding if indicated on the W-9.
    /// Only sends emails for terminal states: COMPLETED (success) or INVALID/BOUNCED/ORDER_NOT_CREATED (failure).
    /// When W-9 is COMPLETED, initiates Instant TIN Matching to verify the TIN/name combination with the IRS.
    /// </summary>
    private async Task<IActionResult> HandleFormW9WebhookAsync(JsonElement formW9, string rawBody)
    {
        string? userEmail = null;
        string? submissionId = null;
        var baseUrl = GetBaseUrl();

        try
        {
            // Extract key fields
            string? payeeRef = null;
            string? w9Status = null;
            string? recipientId = null;
            bool subjectToBackupWithholding = false;

            if (formW9.TryGetProperty("SubmissionId", out var submissionIdElement))
            {
                submissionId = submissionIdElement.GetString();
            }

            if (formW9.TryGetProperty("PayeeRef", out var payeeRefElement))
            {
                payeeRef = payeeRefElement.GetString();
                userEmail = payeeRef; // PayeeRef is typically the email
            }

            if (formW9.TryGetProperty("W9Status", out var w9StatusElement))
            {
                w9Status = w9StatusElement.GetString();
            }

            if (formW9.TryGetProperty("RecipientId", out var recipientIdElement))
            {
                recipientId = recipientIdElement.GetString();
            }

            // Extract backup withholding flag from FormData or BackupWithholding section
            subjectToBackupWithholding = ExtractBackupWithholdingFromW9(formW9);

            _logger.LogInformation(
                "Processing W-9 webhook: SubmissionId={SubmissionId}, PayeeRef={PayeeRef}, Status={Status}, BackupWithholding={BackupWithholding}",
                submissionId, payeeRef, w9Status, subjectToBackupWithholding);

            // Find and update the W9Request record
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            W9Request? w9Request = null;

            // Try to find by submission ID first
            if (!string.IsNullOrWhiteSpace(submissionId))
            {
                w9Request = await context.W9Requests
                    .FirstOrDefaultAsync(w => w.SubmissionId == submissionId);
            }

            // If not found, try to find by email (PayeeRef is the email)
            if (w9Request == null && !string.IsNullOrWhiteSpace(payeeRef))
            {
                w9Request = await context.W9Requests
                    .Where(w => w.Email == payeeRef)
                    .OrderByDescending(w => w.CreatedAt)
                    .FirstOrDefaultAsync();
            }

            if (w9Request == null)
            {
                _logger.LogWarning(
                    "Could not find W9Request for webhook: SubmissionId={SubmissionId}, PayeeRef={PayeeRef}",
                    submissionId, payeeRef);

                // Send processing error email if we have a user email
                if (!string.IsNullOrWhiteSpace(userEmail))
                {
                    await _creatorEmailService.SendTaxFormProcessingErrorEmailAsync(
                        userEmail, baseUrl, submissionId, "W9Request record not found in database.");
                }

                // Return 200 OK to prevent TaxBandits from retrying
                return Ok(new { status = "request_not_found" });
            }

            // Update userEmail from w9Request if we didn't have it
            userEmail ??= w9Request.Email;

            // Update the W9Request record
            w9Request.Status = w9Status;
            w9Request.UpdatedAt = DateTime.UtcNow;
            w9Request.SubjectToBackupWithholding = subjectToBackupWithholding;

            var isW9Completed = string.Equals(w9Status, StatusCompleted, StringComparison.OrdinalIgnoreCase);
            var isFormStatusFailure = !string.IsNullOrWhiteSpace(w9Status) && FailureStatuses.Contains(w9Status);

            if (isW9Completed)
            {
                // W-9 form completed — initiate Instant TIN Matching
                _logger.LogInformation(
                    "W-9 completed for user {UserId}. Initiating Instant TIN Matching. BackupWithholding={BackupWithholding}",
                    w9Request.UserId, subjectToBackupWithholding);

                // Extract TIN matching request data from the webhook payload
                var tinMatchRequest = ExtractTinMatchRequestFromW9(formW9, w9Request.UserId, userEmail);

                if (tinMatchRequest != null)
                {

                    // Call Instant TIN Matching API
                    var tinMatchResponse = await _taxBanditsService.RequestInstantTinMatchAsync(tinMatchRequest);

                    if (tinMatchResponse.Success)
                    {
                        await HandleInstantTinMatchResultAsync(
                            tinMatchResponse, w9Request, formW9, subjectToBackupWithholding, submissionId, userEmail, baseUrl);
                    }
                    else
                    {
                        // Instant TIN Match API call failed — log error but don't fail the webhook
                        _logger.LogError(
                            "Instant TIN Match API call failed for user {UserId}: {Error}",
                            w9Request.UserId, tinMatchResponse.ErrorMessage);

                        // Update tax form status to TinMatchInProgress and let admin handle it
                        var creator = await _creatorService.GetCreatorByUserIdAsync(w9Request.UserId);
                        if (creator != null)
                        {
                            await _creatorService.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.TinMatchInProgress);
                        }

                        await BroadcastWebhookStatusAsync(
                            w9Request.UserId,
                            "TaxFormStatus",
                            true,
                            "Your tax form is completed. TIN verification is in progress.",
                            "TinMatchInProgress");
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Could not extract TIN match request data from W-9 webhook for user {UserId}",
                        w9Request.UserId);

                    // Cannot perform TIN matching without the required data
                    if (!string.IsNullOrWhiteSpace(userEmail))
                    {
                        await _creatorEmailService.SendTaxFormProcessingErrorEmailAsync(
                            userEmail, baseUrl, submissionId, "Could not extract TIN information from the completed W-9 form.");
                    }
                }
            }
            else if (isFormStatusFailure)
            {
                // W-9 form status indicates failure (INVALID, BOUNCED, ORDER_NOT_CREATED)
                _logger.LogWarning("W-9 form failed for user {UserId}. W9Status={W9Status}",
                    w9Request.UserId, w9Status);

                if (!string.IsNullOrWhiteSpace(userEmail))
                {
                    await _creatorEmailService.SendTaxFormFailedEmailAsync(userEmail, baseUrl, "W-9", w9Status);
                }

                await BroadcastWebhookStatusAsync(
                    w9Request.UserId,
                    "TaxFormStatus",
                    false,
                    $"Tax form status: {w9Status}",
                    w9Status);
            }
            else
            {
                // Intermediate status - just log and update, don't send emails
                _logger.LogInformation(
                    "W-9 intermediate status for user {UserId}. W9Status={W9Status}. No email sent.",
                    w9Request.UserId, w9Status);
            }

            await context.SaveChangesAsync();

            _logger.LogInformation(
                "Successfully processed W-9 webhook for user {UserId}. Status: {Status}, IsCompleted: {IsCompleted}, BackupWithholding: {BackupWithholding}",
                w9Request.UserId, w9Status, w9Request.IsCompleted, subjectToBackupWithholding);

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling W-9 webhook");

            // Send processing error email if we have a user email
            if (!string.IsNullOrWhiteSpace(userEmail))
            {
                await _creatorEmailService.SendTaxFormProcessingErrorEmailAsync(
                    userEmail, baseUrl, submissionId, ex.Message);
            }

            return StatusCode(500, "Error processing webhook");
        }
    }

    /// <summary>
    /// Extracts TIN matching request data from a W-9 webhook FormData payload.
    /// </summary>
    private InstantTinMatchRequest? ExtractTinMatchRequestFromW9(JsonElement formW9, int userId, string? email)
    {
        try
        {
            if (!formW9.TryGetProperty("FormData", out var formData) || formData.ValueKind == JsonValueKind.Null)
            {
                _logger.LogWarning("W-9 webhook missing FormData for user {UserId}", userId);
                return null;
            }

            string? tinType = null;
            string? tin = null;
            string? firstName = null;
            string? lastName = null;
            string? middleName = null;
            string? businessName = null;

            if (formData.TryGetProperty("TINType", out var tinTypeEl))
                tinType = tinTypeEl.GetString();
            if (formData.TryGetProperty("TIN", out var tinEl))
                tin = tinEl.GetString();
            if (formData.TryGetProperty("FirstNm", out var firstNameEl))
                firstName = firstNameEl.GetString();
            if (formData.TryGetProperty("LastNm", out var lastNameEl))
                lastName = lastNameEl.GetString();
            if (formData.TryGetProperty("MiddleNm", out var middleNameEl))
                middleName = middleNameEl.GetString();

            // For EIN, the business name is in Line1Nm
            if (formData.TryGetProperty("Line1Nm", out var line1NmEl))
                businessName = line1NmEl.GetString();

            if (string.IsNullOrWhiteSpace(tinType) || string.IsNullOrWhiteSpace(tin))
            {
                _logger.LogWarning("W-9 webhook missing TINType or TIN for user {UserId}", userId);
                return null;
            }

            return new InstantTinMatchRequest
            {
                TINType = tinType,
                TIN = tin,
                FirstNm = firstName,
                LastNm = lastName,
                MiddleNm = middleName,
                BusinessNm = businessName,
                UserId = userId,
                Email = email
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting TIN match request from W-9 webhook for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Handles the result of an Instant TIN Match request.
    /// TIN-001 (SUCCESS): Activate creator. TIN-002 (FAILED): Send failure email. TIN-003 (ON HOLD): Wait for webhook.
    /// </summary>
    private async Task HandleInstantTinMatchResultAsync(
        InstantTinMatchResponse tinMatchResponse,
        W9Request w9Request,
        JsonElement formW9,
        bool subjectToBackupWithholding,
        string? submissionId,
        string? userEmail,
        string baseUrl)
    {
        var tinStatusCode = tinMatchResponse.TINStatusCode;

        _logger.LogInformation(
            "Instant TIN Match result for user {UserId}: StatusCode={TINStatusCode}, Status={TINStatus}",
            w9Request.UserId, tinStatusCode, tinMatchResponse.TINStatus);

        if (string.Equals(tinStatusCode, TinStatusCodeSuccess, StringComparison.OrdinalIgnoreCase))
        {
            // TIN-001: SUCCESS - TIN and name match IRS records
            w9Request.IsCompleted = true;
            w9Request.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "Instant TIN Match passed for user {UserId}. Proceeding with creator role assignment. BackupWithholding={BackupWithholding}",
                w9Request.UserId, subjectToBackupWithholding);

            Guid? submissionGuid = null;
            if (Guid.TryParse(submissionId, out var parsedGuid))
            {
                submissionGuid = parsedGuid;
            }

            var withholdingRate = subjectToBackupWithholding ? BackupWithholdingRate : 0m;

            await CompleteCreatorOnboardingWithTaxDataAsync(
                w9Request.UserId,
                TaxResidencyType.US,
                taxResidencyCountry: "US",
                withholdingRate: withholdingRate,
                taxFormExpirationDate: null,
                taxBanditsSubmissionId: submissionGuid,
                subjectToBackupWithholding: subjectToBackupWithholding);

            await BroadcastWebhookStatusAsync(
                w9Request.UserId,
                "TaxFormCompleted",
                true,
                "Your tax form has been completed successfully!",
                "Completed");

            if (!string.IsNullOrWhiteSpace(userEmail))
            {
                await _creatorEmailService.SendTaxFormSuccessEmailAsync(userEmail, baseUrl, "W-9");
            }
        }
        else if (string.Equals(tinStatusCode, TinStatusCodeFailed, StringComparison.OrdinalIgnoreCase))
        {
            // TIN-002: FAILED - TIN and name don't match IRS records
            _logger.LogWarning("Instant TIN Match failed for user {UserId}. TINStatusCode={TINStatusCode}",
                w9Request.UserId, tinStatusCode);

            // Record the failure timestamp for 24-hour cooldown enforcement
            var creator = await _creatorService.GetCreatorByUserIdAsync(w9Request.UserId);
            if (creator != null)
            {
                await _creatorService.SetTinMatchFailedAsync(creator.Id);
            }

            if (!string.IsNullOrWhiteSpace(userEmail))
            {
                await _creatorEmailService.SendTaxFormFailedEmailAsync(userEmail, baseUrl, "W-9",
                    "TIN verification failed. Please double-check your Tax Identification Number and resubmit. You may retry after 24 hours.");
            }

            await BroadcastWebhookStatusAsync(
                w9Request.UserId,
                "TaxFormStatus",
                false,
                "TIN verification failed. You may retry after 24 hours.",
                "Failed");
        }
        else if (string.Equals(tinStatusCode, TinStatusCodeOnHold, StringComparison.OrdinalIgnoreCase))
        {
            // TIN-003: ON HOLD - IRS system issues, will get result via webhook later
            _logger.LogInformation(
                "Instant TIN Match on hold for user {UserId}. Will receive status via webhook.",
                w9Request.UserId);

            var creator = await _creatorService.GetCreatorByUserIdAsync(w9Request.UserId);
            if (creator != null)
            {
                await _creatorService.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.TinMatchInProgress);
            }

            await BroadcastWebhookStatusAsync(
                w9Request.UserId,
                "TaxFormStatus",
                true,
                "Your tax form is completed. TIN verification is in progress and will update automatically when complete.",
                "TinMatchInProgress");
        }
        else
        {
            // Unknown status code - log and treat as on hold
            _logger.LogWarning(
                "Unknown Instant TIN Match status code for user {UserId}: {TINStatusCode}",
                w9Request.UserId, tinStatusCode);

            var creator = await _creatorService.GetCreatorByUserIdAsync(w9Request.UserId);
            if (creator != null)
            {
                await _creatorService.UpdateTaxFormStatusAsync(creator.Id, TaxFormStatus.TinMatchInProgress);
            }

            await BroadcastWebhookStatusAsync(
                w9Request.UserId,
                "TaxFormStatus",
                true,
                "Your tax form is completed. TIN verification is in progress.",
                "TinMatchInProgress");
        }
    }

    /// <summary>
    /// Extracts backup withholding flag from W-9 webhook FormData.
    /// TaxBandits includes the backup withholding flag in FormData.IsBackUpWH field.
    /// </summary>
    private bool ExtractBackupWithholdingFromW9(JsonElement formW9)
    {
        try
        {
            // Try to extract from BackupWithholding section
            if (formW9.TryGetProperty("BackupWithholding", out var backupWithholding) &&
                backupWithholding.ValueKind != JsonValueKind.Null)
            {
                if (backupWithholding.TryGetProperty("IsSubjectToBackupWithholding", out var isSubject))
                {
                    var result = isSubject.ValueKind == JsonValueKind.True;
                    _logger.LogInformation("Extracted backup withholding from BackupWithholding section: {Value}", result);
                    return result;
                }
            }

            // Alternative: Check FormData section for backup withholding fields
            if (formW9.TryGetProperty("FormData", out var formData) &&
                formData.ValueKind != JsonValueKind.Null)
            {
                // Check for IsBackUpWH (primary field from TaxBandits W-9 webhook)
                if (formData.TryGetProperty("IsBackUpWH", out var isBackUpWH))
                {
                    var result = isBackUpWH.ValueKind == JsonValueKind.True;
                    _logger.LogInformation("Extracted backup withholding from FormData.IsBackUpWH: {Value}", result);
                    return result;
                }

                if (formData.TryGetProperty("SubjectToBackupWithholding", out var subjectField))
                {
                    var result = subjectField.ValueKind == JsonValueKind.True;
                    _logger.LogInformation("Extracted backup withholding from FormData.SubjectToBackupWithholding: {Value}", result);
                    return result;
                }

                // Check for IsSubjectToBackupWithholding in FormData
                if (formData.TryGetProperty("IsSubjectToBackupWithholding", out var isSubjectFormData))
                {
                    var result = isSubjectFormData.ValueKind == JsonValueKind.True;
                    _logger.LogInformation("Extracted backup withholding from FormData.IsSubjectToBackupWithholding: {Value}", result);
                    return result;
                }
            }

            _logger.LogInformation("No backup withholding flag found in W-9 webhook, defaulting to false");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting backup withholding from W-9 webhook, defaulting to false");
            return false;
        }
    }

    /// <summary>
    /// Handles W-8BEN form status change webhook (for non-US persons).
    /// Extracts tax residency data. No withholding is applied for W-8 creators.
    /// Only sends emails for terminal states: COMPLETED (success) or INVALID/BOUNCED/ORDER_NOT_CREATED (failure).
    /// W-8 forms do not require TIN matching.
    /// </summary>
    private async Task<IActionResult> HandleFormW8WebhookAsync(JsonElement formW8, string rawBody)
    {
        string? userEmail = null;
        string? submissionId = null;
        var baseUrl = GetBaseUrl();

        try
        {
            // Extract key fields - W-8BEN uses similar structure
            string? payeeRef = null;
            string? w8Status = null;

            if (formW8.TryGetProperty("SubmissionId", out var submissionIdElement))
            {
                submissionId = submissionIdElement.GetString();
            }

            if (formW8.TryGetProperty("PayeeRef", out var payeeRefElement))
            {
                payeeRef = payeeRefElement.GetString();
                userEmail = payeeRef; // PayeeRef is typically the email
            }

            // W-8BEN uses W8BENStatus instead of W9Status
            if (formW8.TryGetProperty("W8BENStatus", out var w8StatusElement))
            {
                w8Status = w8StatusElement.GetString();
            }

            _logger.LogInformation(
                "Processing W-8BEN webhook: SubmissionId={SubmissionId}, PayeeRef={PayeeRef}, Status={Status}",
                submissionId, payeeRef, w8Status);

            // Find and update the W9Request record (we use the same table for both W-9 and W-8)
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            W9Request? w9Request = null;

            if (!string.IsNullOrWhiteSpace(submissionId))
            {
                w9Request = await context.W9Requests
                    .FirstOrDefaultAsync(w => w.SubmissionId == submissionId);
            }

            if (w9Request == null && !string.IsNullOrWhiteSpace(payeeRef))
            {
                w9Request = await context.W9Requests
                    .Where(w => w.Email == payeeRef)
                    .OrderByDescending(w => w.CreatedAt)
                    .FirstOrDefaultAsync();
            }

            if (w9Request == null)
            {
                _logger.LogWarning(
                    "Could not find W9Request for W-8BEN webhook: SubmissionId={SubmissionId}, PayeeRef={PayeeRef}",
                    submissionId, payeeRef);

                // Send processing error email if we have a user email
                if (!string.IsNullOrWhiteSpace(userEmail))
                {
                    await _creatorEmailService.SendTaxFormProcessingErrorEmailAsync(
                        userEmail, baseUrl, submissionId, "W9Request record not found in database.");
                }

                return Ok(new { status = "request_not_found" });
            }

            // Update userEmail from w9Request if we didn't have it
            userEmail ??= w9Request.Email;

            // Update the W9Request record
            w9Request.Status = w8Status;
            w9Request.UpdatedAt = DateTime.UtcNow;

            var isW8Completed = string.Equals(w8Status, StatusCompleted, StringComparison.OrdinalIgnoreCase);
            var isFormStatusFailure = !string.IsNullOrWhiteSpace(w8Status) && FailureStatuses.Contains(w8Status);

            if (isW8Completed)
            {
                // W-8 fully successful: form completed (W-8 doesn't require TIN matching)
                w9Request.IsCompleted = true;
                w9Request.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation(
                    "W-8BEN completed for user {UserId}. Proceeding with creator role assignment and tax data extraction.",
                    w9Request.UserId);

                // Extract W-8BEN tax residency data from FormData
                var taxData = ExtractW8BenTaxData(formW8);
                
                // Parse submission ID as Guid if possible
                Guid? submissionGuid = null;
                if (Guid.TryParse(submissionId, out var parsedGuid))
                {
                    submissionGuid = parsedGuid;
                }

                // Complete creator onboarding with tax data
                await CompleteCreatorOnboardingWithTaxDataAsync(
                    w9Request.UserId,
                    TaxResidencyType.Foreign,
                    taxData.TaxResidencyCountry,
                    taxData.WithholdingRate,
                    taxData.ExpirationDate,
                    submissionGuid);

                // Broadcast SignalR update to notify the user's browser
                await BroadcastWebhookStatusAsync(
                    w9Request.UserId,
                    "TaxFormCompleted",
                    true,
                    "Your tax form has been completed successfully!",
                    "Completed");

                // Send success/welcome email to user and admin notification (with country code for W-8)
                if (!string.IsNullOrWhiteSpace(userEmail))
                {
                    await _creatorEmailService.SendTaxFormSuccessEmailAsync(userEmail, baseUrl, "W-8", taxData.TaxResidencyCountry);
                }
            }
            else if (isFormStatusFailure)
            {
                // W-8 form status indicates failure (INVALID, BOUNCED, ORDER_NOT_CREATED)
                _logger.LogWarning("W-8 form failed for user {UserId}. W8Status={W8Status}",
                    w9Request.UserId, w8Status);

                if (!string.IsNullOrWhiteSpace(userEmail))
                {
                    await _creatorEmailService.SendTaxFormFailedEmailAsync(userEmail, baseUrl, "W-8", w8Status);
                }

                await BroadcastWebhookStatusAsync(
                    w9Request.UserId,
                    "TaxFormStatus",
                    false,
                    $"Tax form status: {w8Status}",
                    w8Status);
            }
            else
            {
                // Intermediate status - just log and update, don't send emails
                _logger.LogInformation(
                    "W-8 intermediate status for user {UserId}. W8Status={W8Status}. No email sent.",
                    w9Request.UserId, w8Status);
            }

            await context.SaveChangesAsync();

            _logger.LogInformation(
                "Successfully processed W-8BEN webhook for user {UserId}. Status: {Status}, IsCompleted: {IsCompleted}",
                w9Request.UserId, w8Status, w9Request.IsCompleted);

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling W-8BEN webhook");

            // Send processing error email if we have a user email
            if (!string.IsNullOrWhiteSpace(userEmail))
            {
                await _creatorEmailService.SendTaxFormProcessingErrorEmailAsync(
                    userEmail, baseUrl, submissionId, ex.Message);
            }

            return StatusCode(500, "Error processing webhook");
        }
    }

    /// <summary>
    /// Extracts tax residency data from W-8BEN FormData.
    /// No withholding is applied for W-8 creators.
    /// </summary>
    private (string? TaxResidencyCountry, decimal WithholdingRate, DateTime? ExpirationDate) ExtractW8BenTaxData(JsonElement formW8)
    {
        string? taxResidencyCountry = null;
        decimal withholdingRate = 0m; // No withholding for W-8 creators
        DateTime? expirationDate = null;

        try
        {
            if (formW8.TryGetProperty("FormData", out var formData))
            {
                // Extract CitizenOfCountry and convert to ISO-2 code
                if (formData.TryGetProperty("CitizenOfCountry", out var citizenOfCountry))
                {
                    var countryName = citizenOfCountry.GetString();
                    taxResidencyCountry = ConvertCountryNameToIso2(countryName);
                    _logger.LogInformation("Extracted tax residency country: {Country} -> {Iso2}", countryName, taxResidencyCountry);
                }

                // Extract expiration date
                if (formData.TryGetProperty("ExpiryDate", out var expiryDateElement))
                {
                    var expiryDateStr = expiryDateElement.GetString();
                    if (!string.IsNullOrWhiteSpace(expiryDateStr) && DateTime.TryParse(expiryDateStr, out var parsedDate))
                    {
                        expirationDate = parsedDate;
                        _logger.LogInformation("Extracted tax form expiration date: {Date}", expirationDate);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting W-8BEN tax data, using defaults");
        }

        return (taxResidencyCountry, withholdingRate, expirationDate);
    }

    /// <summary>
    /// Converts a country name to ISO-2 country code.
    /// This is a simplified implementation - in production, consider:
    /// - Using a library like NuGet package ISO3166 for comprehensive country data
    /// - Extracting mappings to a configuration file or database
    /// - Using TaxBandits' country code directly if available in the response
    /// </summary>
    private static string? ConvertCountryNameToIso2(string? countryName)
    {
        if (string.IsNullOrWhiteSpace(countryName))
            return null;

        // Handle special case of "Other Country" first
        if (countryName.Equals("Other Country", StringComparison.OrdinalIgnoreCase))
            return null;

        // Comprehensive country name to ISO-2 code mappings
        var countryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // A
            { "Afghanistan", "AF" },
            { "Albania", "AL" },
            { "Algeria", "DZ" },
            { "Andorra", "AD" },
            { "Angola", "AO" },
            { "Antigua and Barbuda", "AG" },
            { "Argentina", "AR" },
            { "Armenia", "AM" },
            { "Australia", "AU" },
            { "Austria", "AT" },
            { "Azerbaijan", "AZ" },
            // B
            { "Bahamas", "BS" },
            { "Bahrain", "BH" },
            { "Bangladesh", "BD" },
            { "Barbados", "BB" },
            { "Belarus", "BY" },
            { "Belgium", "BE" },
            { "Belize", "BZ" },
            { "Benin", "BJ" },
            { "Bhutan", "BT" },
            { "Bolivia", "BO" },
            { "Bosnia and Herzegovina", "BA" },
            { "Botswana", "BW" },
            { "Brazil", "BR" },
            { "Brunei", "BN" },
            { "Bulgaria", "BG" },
            { "Burkina Faso", "BF" },
            { "Burundi", "BI" },
            // C
            { "Cambodia", "KH" },
            { "Cameroon", "CM" },
            { "Canada", "CA" },
            { "Cape Verde", "CV" },
            { "Central African Republic", "CF" },
            { "Chad", "TD" },
            { "Chile", "CL" },
            { "China", "CN" },
            { "Colombia", "CO" },
            { "Comoros", "KM" },
            { "Congo", "CG" },
            { "Costa Rica", "CR" },
            { "Croatia", "HR" },
            { "Cuba", "CU" },
            { "Cyprus", "CY" },
            { "Czech Republic", "CZ" },
            { "Czechia", "CZ" },
            // D
            { "Denmark", "DK" },
            { "Djibouti", "DJ" },
            { "Dominica", "DM" },
            { "Dominican Republic", "DO" },
            // E
            { "Ecuador", "EC" },
            { "Egypt", "EG" },
            { "El Salvador", "SV" },
            { "Equatorial Guinea", "GQ" },
            { "Eritrea", "ER" },
            { "Estonia", "EE" },
            { "Eswatini", "SZ" },
            { "Ethiopia", "ET" },
            // F
            { "Fiji", "FJ" },
            { "Finland", "FI" },
            { "France", "FR" },
            // G
            { "Gabon", "GA" },
            { "Gambia", "GM" },
            { "Georgia", "GE" },
            { "Germany", "DE" },
            { "Ghana", "GH" },
            { "Greece", "GR" },
            { "Grenada", "GD" },
            { "Guatemala", "GT" },
            { "Guinea", "GN" },
            { "Guinea-Bissau", "GW" },
            { "Guyana", "GY" },
            // H
            { "Haiti", "HT" },
            { "Honduras", "HN" },
            { "Hong Kong", "HK" },
            { "Hungary", "HU" },
            // I
            { "Iceland", "IS" },
            { "India", "IN" },
            { "Indonesia", "ID" },
            { "Iran", "IR" },
            { "Iraq", "IQ" },
            { "Ireland", "IE" },
            { "Israel", "IL" },
            { "Italy", "IT" },
            { "Ivory Coast", "CI" },
            // J
            { "Jamaica", "JM" },
            { "Japan", "JP" },
            { "Jordan", "JO" },
            // K
            { "Kazakhstan", "KZ" },
            { "Kenya", "KE" },
            { "Kiribati", "KI" },
            { "Korea", "KR" },
            { "South Korea", "KR" },
            { "North Korea", "KP" },
            { "Kuwait", "KW" },
            { "Kyrgyzstan", "KG" },
            // L
            { "Laos", "LA" },
            { "Latvia", "LV" },
            { "Lebanon", "LB" },
            { "Lesotho", "LS" },
            { "Liberia", "LR" },
            { "Libya", "LY" },
            { "Liechtenstein", "LI" },
            { "Lithuania", "LT" },
            { "Luxembourg", "LU" },
            // M
            { "Madagascar", "MG" },
            { "Malawi", "MW" },
            { "Malaysia", "MY" },
            { "Maldives", "MV" },
            { "Mali", "ML" },
            { "Malta", "MT" },
            { "Marshall Islands", "MH" },
            { "Mauritania", "MR" },
            { "Mauritius", "MU" },
            { "Mexico", "MX" },
            { "Micronesia", "FM" },
            { "Moldova", "MD" },
            { "Monaco", "MC" },
            { "Mongolia", "MN" },
            { "Montenegro", "ME" },
            { "Morocco", "MA" },
            { "Mozambique", "MZ" },
            { "Myanmar", "MM" },
            // N
            { "Namibia", "NA" },
            { "Nauru", "NR" },
            { "Nepal", "NP" },
            { "Netherlands", "NL" },
            { "New Zealand", "NZ" },
            { "Nicaragua", "NI" },
            { "Niger", "NE" },
            { "Nigeria", "NG" },
            { "Norway", "NO" },
            // O
            { "Oman", "OM" },
            // P
            { "Pakistan", "PK" },
            { "Palau", "PW" },
            { "Palestine", "PS" },
            { "Panama", "PA" },
            { "Papua New Guinea", "PG" },
            { "Paraguay", "PY" },
            { "Peru", "PE" },
            { "Philippines", "PH" },
            { "Poland", "PL" },
            { "Portugal", "PT" },
            // Q
            { "Qatar", "QA" },
            // R
            { "Romania", "RO" },
            { "Russia", "RU" },
            { "Russian Federation", "RU" },
            { "Rwanda", "RW" },
            // S
            { "Saint Kitts and Nevis", "KN" },
            { "Saint Lucia", "LC" },
            { "Saint Vincent and the Grenadines", "VC" },
            { "Samoa", "WS" },
            { "San Marino", "SM" },
            { "Saudi Arabia", "SA" },
            { "Senegal", "SN" },
            { "Serbia", "RS" },
            { "Seychelles", "SC" },
            { "Sierra Leone", "SL" },
            { "Singapore", "SG" },
            { "Slovakia", "SK" },
            { "Slovenia", "SI" },
            { "Solomon Islands", "SB" },
            { "Somalia", "SO" },
            { "South Africa", "ZA" },
            { "South Sudan", "SS" },
            { "Spain", "ES" },
            { "Sri Lanka", "LK" },
            { "Sudan", "SD" },
            { "Suriname", "SR" },
            { "Sweden", "SE" },
            { "Switzerland", "CH" },
            { "Syria", "SY" },
            // T
            { "Taiwan", "TW" },
            { "Tajikistan", "TJ" },
            { "Tanzania", "TZ" },
            { "Thailand", "TH" },
            { "Timor-Leste", "TL" },
            { "Togo", "TG" },
            { "Tonga", "TO" },
            { "Trinidad and Tobago", "TT" },
            { "Tunisia", "TN" },
            { "Turkey", "TR" },
            { "Turkmenistan", "TM" },
            { "Tuvalu", "TV" },
            // U
            { "Uganda", "UG" },
            { "Ukraine", "UA" },
            { "United Arab Emirates", "AE" },
            { "UAE", "AE" },
            { "United Kingdom", "GB" },
            { "UK", "GB" },
            { "Uruguay", "UY" },
            { "Uzbekistan", "UZ" },
            // V
            { "Vanuatu", "VU" },
            { "Vatican City", "VA" },
            { "Venezuela", "VE" },
            { "Vietnam", "VN" },
            // Y
            { "Yemen", "YE" },
            // Z
            { "Zambia", "ZM" },
            { "Zimbabwe", "ZW" }
        };

        if (countryMap.TryGetValue(countryName, out var iso2))
            return iso2;

        // If country name is already a 2-letter code, return it
        if (countryName.Length == 2)
            return countryName.ToUpperInvariant();

        return null;
    }

    /// <summary>
    /// Completes the creator onboarding process with tax residency data.
    /// Updates tax form status, stores tax data, and adds Creator role if both onboarding processes are complete.
    /// </summary>
    private async Task CompleteCreatorOnboardingWithTaxDataAsync(
        int userId,
        TaxResidencyType taxResidencyType,
        string? taxResidencyCountry,
        decimal withholdingRate,
        DateTime? taxFormExpirationDate,
        Guid? taxBanditsSubmissionId,
        bool subjectToBackupWithholding = false)
    {
        try
        {
            // Get the creator record
            var creator = await _creatorService.GetCreatorByUserIdAsync(userId);
            if (creator == null)
            {
                _logger.LogWarning("No creator record found for user {UserId}", userId);
                return;
            }

            // Update the creator's tax form status and tax residency data
            await _creatorService.UpdateTaxFormStatusWithTaxDataAsync(
                creator.Id, 
                TaxFormStatus.Completed,
                taxResidencyType,
                taxResidencyCountry,
                withholdingRate,
                taxFormExpirationDate,
                taxBanditsSubmissionId,
                subjectToBackupWithholding);

            _logger.LogInformation(
                "Updated creator {CreatorId} tax data: ResidencyType={ResidencyType}, Country={Country}, WithholdingRate={Rate:P2}, BackupWithholding={BackupWithholding}",
                creator.Id, taxResidencyType, taxResidencyCountry, withholdingRate, subjectToBackupWithholding);

            // Reload creator to get updated status
            creator = await _creatorService.GetCreatorByUserIdAsync(userId);
            if (creator == null)
            {
                _logger.LogWarning("Creator record not found after update for user {UserId}", userId);
                return;
            }

            // Check if PayPal onboarding is also complete.
            // For returning creators who re-signed up, OnboardingStatus should be Completed.
            // However, if it's Suspended/ConsentRevoked but PayPalAccountAffirmed is true,
            // it means the creator completed the re-signup form but OnboardingStatus wasn't
            // properly reset — fix it defensively and proceed with activation.
            if (creator.OnboardingStatus != CreatorOnboardingStatus.Completed)
            {
                if (creator.PayPalAccountAffirmed)
                {
                    _logger.LogWarning(
                        "User {UserId} completed tax form and has PayPalAccountAffirmed=true but OnboardingStatus={Status}. Resetting to Completed.",
                        userId, creator.OnboardingStatus);
                    await _creatorService.UpdateOnboardingStatusAsync(creator.Id, CreatorOnboardingStatus.Completed);
                }
                else
                {
                    _logger.LogInformation(
                        "User {UserId} completed tax form but PayPal onboarding is not complete. PayPal Status: {PayPalStatus}, Tax Form Status: {TaxFormStatus}",
                        userId, creator.OnboardingStatus, creator.TaxFormStatus);
                    return;
                }
            }

            // Both onboarding processes are complete - add Creator role
            _logger.LogInformation(
                "Both PayPal and tax form onboarding complete for user {UserId}. Adding Creator role.",
                userId);

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
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user != null && !await _userManager.IsInRoleAsync(user, Common.Helpers.Roles.Creator))
            {
                await _userManager.AddToRoleAsync(user, Common.Helpers.Roles.Creator);
                _logger.LogInformation("Added Creator role to user {UserId} after both PayPal and tax form completion", userId);
            }

            // Activate the creator
            await _creatorService.ActivateCreatorAsync(creator.Id);
            _logger.LogInformation("Activated creator {CreatorId} for user {UserId}", creator.Id, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing creator onboarding for user {UserId}", userId);
        }
    }

    /// <summary>
    /// Verifies the TaxBandits webhook signature.
    /// See: https://developer.taxbandits.com/docs/webhooks/validatingrequests/
    /// </summary>
    private bool VerifyWebhookSignature(string body)
    {
        try
        {
            var clientId = _configuration["TaxBandits:ClientId"];
            var clientSecret = _configuration["TaxBandits:ClientSecret"];

            // If credentials are not configured, skip verification in development
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogWarning("TaxBandits credentials not configured - skipping webhook signature verification");
                return true;
            }

            // Get the signature and timestamp from headers
            // Note: Header names may vary in casing, try both variations
            var signature = Request.Headers["Signature"].FirstOrDefault();
            var timestamp = Request.Headers["TimeStamp"].FirstOrDefault() 
                         ?? Request.Headers["Timestamp"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp))
            {
                _logger.LogWarning("Missing TaxBandits webhook signature headers. Signature present: {SigPresent}, Timestamp present: {TsPresent}", 
                    !string.IsNullOrWhiteSpace(signature), !string.IsNullOrWhiteSpace(timestamp));
                return false;
            }

            // Log for debugging
            _logger.LogInformation("TaxBandits webhook signature validation - Timestamp: {Timestamp}", timestamp);

            // Per TaxBandits docs: message = ClientId + '\n' + Timestamp
            // Then compute HMAC-SHA256 with ClientSecret and compare base64 result
            var message = $"{clientId}\n{timestamp}";
            var expectedSignature = ComputeHmacSha256Base64(message, clientSecret);

            _logger.LogDebug("TaxBandits signature validation - Message: {Message}, ClientId: {ClientId}", 
                message.Replace("\n", "\\n"), clientId);

            // Use timing-safe comparison
            var isValid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expectedSignature));

            if (!isValid)
            {
                _logger.LogWarning(
                    "TaxBandits webhook signature mismatch. Expected: {Expected}, Received: {Received}, Message used: {Message}",
                    expectedSignature, signature, message.Replace("\n", "\\n"));
            }
            else
            {
                _logger.LogInformation("TaxBandits webhook signature validated successfully");
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying TaxBandits webhook signature");
            return false;
        }
    }

    /// <summary>
    /// Computes HMAC-SHA256 and returns the result as a base64 string.
    /// </summary>
    private static string ComputeHmacSha256Base64(string message, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(messageBytes);
        return Convert.ToBase64String(hashBytes);
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
        return _configuration["App:BaseUrl"] ?? "https://streamtunes.net";
    }
}
