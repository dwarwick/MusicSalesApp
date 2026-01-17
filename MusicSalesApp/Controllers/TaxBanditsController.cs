#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<TaxBanditsController> _logger;

    // W-9/W-8 completion status
    private const string StatusCompleted = "COMPLETED";

    public TaxBanditsController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        ICreatorService creatorService,
        IConfiguration configuration,
        ILogger<TaxBanditsController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
        _roleManager = roleManager;
        _creatorService = creatorService;
        _configuration = configuration;
        _logger = logger;
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

            // Handle W-9 form completion
            if (formType == "FormW9" && root.TryGetProperty("FormW9", out var formW9))
            {
                return await HandleFormW9WebhookAsync(formW9, body);
            }
            // Handle W-8 form completion (for non-US persons)
            else if (formType == "FormW8BEN" && root.TryGetProperty("FormW8BEN", out var formW8))
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
    /// Handles W-9 form status change webhook.
    /// </summary>
    private async Task<IActionResult> HandleFormW9WebhookAsync(JsonElement formW9, string rawBody)
    {
        try
        {
            // Extract key fields
            string? submissionId = null;
            string? payeeRef = null;
            string? w9Status = null;
            string? recipientId = null;

            if (formW9.TryGetProperty("SubmissionId", out var submissionIdElement))
            {
                submissionId = submissionIdElement.GetString();
            }

            if (formW9.TryGetProperty("PayeeRef", out var payeeRefElement))
            {
                payeeRef = payeeRefElement.GetString();
            }

            if (formW9.TryGetProperty("W9Status", out var w9StatusElement))
            {
                w9Status = w9StatusElement.GetString();
            }

            if (formW9.TryGetProperty("RecipientId", out var recipientIdElement))
            {
                recipientId = recipientIdElement.GetString();
            }

            _logger.LogInformation(
                "Processing W-9 webhook: SubmissionId={SubmissionId}, PayeeRef={PayeeRef}, Status={Status}",
                submissionId, payeeRef, w9Status);

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
                // Return 200 OK to prevent TaxBandits from retrying
                return Ok(new { status = "request_not_found" });
            }

            // Update the W9Request record
            w9Request.Status = w9Status;
            w9Request.UpdatedAt = DateTime.UtcNow;
            w9Request.RawResponse = rawBody;

            if (w9Status == StatusCompleted)
            {
                w9Request.IsCompleted = true;
                w9Request.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation(
                    "W-9 completed for user {UserId}. Proceeding with creator role assignment.",
                    w9Request.UserId);

                // Complete the creator onboarding - add Creator role
                await CompleteCreatorOnboardingAsync(w9Request.UserId);
            }

            await context.SaveChangesAsync();

            _logger.LogInformation(
                "Successfully processed W-9 webhook for user {UserId}. Status: {Status}, IsCompleted: {IsCompleted}",
                w9Request.UserId, w9Status, w9Request.IsCompleted);

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling W-9 webhook");
            return StatusCode(500, "Error processing webhook");
        }
    }

    /// <summary>
    /// Handles W-8BEN form status change webhook (for non-US persons).
    /// </summary>
    private async Task<IActionResult> HandleFormW8WebhookAsync(JsonElement formW8, string rawBody)
    {
        try
        {
            // Extract key fields - W-8BEN uses similar structure
            string? submissionId = null;
            string? payeeRef = null;
            string? w8Status = null;

            if (formW8.TryGetProperty("SubmissionId", out var submissionIdElement))
            {
                submissionId = submissionIdElement.GetString();
            }

            if (formW8.TryGetProperty("PayeeRef", out var payeeRefElement))
            {
                payeeRef = payeeRefElement.GetString();
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
                return Ok(new { status = "request_not_found" });
            }

            // Update the W9Request record
            w9Request.Status = w8Status;
            w9Request.UpdatedAt = DateTime.UtcNow;
            w9Request.RawResponse = rawBody;

            if (w8Status == StatusCompleted)
            {
                w9Request.IsCompleted = true;
                w9Request.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation(
                    "W-8BEN completed for user {UserId}. Proceeding with creator role assignment.",
                    w9Request.UserId);

                await CompleteCreatorOnboardingAsync(w9Request.UserId);
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
            return StatusCode(500, "Error processing webhook");
        }
    }

    /// <summary>
    /// Completes the creator onboarding process by adding the Creator role to the user.
    /// </summary>
    private async Task CompleteCreatorOnboardingAsync(int userId)
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

            // Check if PayPal onboarding is also complete
            if (creator.OnboardingStatus != CreatorOnboardingStatus.Completed)
            {
                _logger.LogInformation(
                    "User {UserId} completed tax form but PayPal onboarding is not complete. Status: {Status}",
                    userId, creator.OnboardingStatus);
                return;
            }

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
                _logger.LogInformation("Added Creator role to user {UserId} after W-9/W-8 completion", userId);
            }
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
            var signature = Request.Headers["Signature"].FirstOrDefault();
            var timestamp = Request.Headers["TimeStamp"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp))
            {
                _logger.LogWarning("Missing TaxBandits webhook signature headers");
                return false;
            }

            // Validate timestamp to prevent replay attacks (allow 5 minute window)
            // TaxBandits may send timestamps in various formats, with or without timezone info
            DateTimeOffset? webhookTime = null;
            
            // Try parsing with explicit formats first
            var formats = new[]
            {
                "MM/dd/yyyy hh:mm:ss.fff tt zzz",    // With timezone offset
                "MM/dd/yyyy hh:mm:ss.fff tt",        // Without timezone offset
                "MM/dd/yyyy HH:mm:ss.fff zzz",       // 24-hour with timezone
                "MM/dd/yyyy HH:mm:ss.fff",           // 24-hour without timezone
                "yyyy-MM-dd'T'HH:mm:ss.fffK",        // ISO 8601
                "yyyy-MM-dd HH:mm:ss zzz",           // ISO-like with timezone
            };

            foreach (var format in formats)
            {
                if (DateTimeOffset.TryParseExact(timestamp, format, 
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AllowWhiteSpaces, 
                    out var parsed))
                {
                    webhookTime = parsed;
                    break;
                }
            }

            // Fallback to general parsing if specific formats don't work
            if (!webhookTime.HasValue && DateTimeOffset.TryParse(timestamp, 
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var generalParsed))
            {
                webhookTime = generalParsed;
            }

            if (webhookTime.HasValue)
            {
                var timeDifference = DateTimeOffset.UtcNow - webhookTime.Value.ToUniversalTime();
                if (Math.Abs(timeDifference.TotalMinutes) > 5)
                {
                    _logger.LogWarning(
                        "TaxBandits webhook timestamp is outside acceptable window. Timestamp: {Timestamp}, Parsed: {Parsed}, Current: {Current}, Difference: {Difference} minutes",
                        timestamp, webhookTime.Value.ToUniversalTime(), DateTimeOffset.UtcNow, timeDifference.TotalMinutes);
                    return false;
                }
            }
            else
            {
                _logger.LogWarning("TaxBandits webhook timestamp could not be parsed: {Timestamp}", timestamp);
                // Continue with signature validation - timestamp format may vary
            }

            // Compute expected signature: HMAC-SHA256(clientId + "\n" + timestamp, clientSecret)
            var message = $"{clientId}\n{timestamp}";
            var expectedSignature = ComputeHmacSha256Base64(message, clientSecret);

            // Use timing-safe comparison
            var isValid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expectedSignature));

            if (!isValid)
            {
                _logger.LogWarning(
                    "TaxBandits webhook signature mismatch. Expected: {Expected}, Received: {Received}",
                    expectedSignature, signature);
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
}
