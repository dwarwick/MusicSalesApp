#nullable enable

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Middleware;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

[Route("api/mobile/contact")]
[ApiController]
[RequireMobileApiKey]
[Authorize(Policy = Permissions.ValidatedUser)]
public class MobileContactController : ControllerBase
{
    internal const int MaxMessageLength = 4000;

    private readonly IContactRequestEmailService _contactRequestEmailService;
    private readonly IContactRequestRateLimitService _rateLimitService;
    private readonly ILogger<MobileContactController> _logger;

    public MobileContactController(
        IContactRequestEmailService contactRequestEmailService,
        IContactRequestRateLimitService rateLimitService,
        ILogger<MobileContactController> logger)
    {
        _contactRequestEmailService = contactRequestEmailService;
        _rateLimitService = rateLimitService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] MobileContactRequest request)
    {
        var validationError = ValidateRequest(request);
        if (validationError != null)
        {
            return BadRequest(new { error = validationError });
        }

        if (!TryGetCurrentUser(out var userId, out var userEmail))
        {
            return Unauthorized();
        }

        var subject = request.Subject.Trim();
        var message = request.Message.Trim();
        var ipAddress = ResolveClientIpAddress();
        var reservation = await _rateLimitService.TryReserveSubmissionAsync(
            userId,
            userEmail,
            subject,
            message.Length,
            ipAddress);

        if (!reservation.IsAllowed)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = reservation.ErrorMessage });
        }

        var emailResult = await _contactRequestEmailService.SendContactRequestEmailsAsync(userEmail, subject, message);
        if (reservation.SubmissionId.HasValue)
        {
            await _rateLimitService.MarkEmailResultAsync(
                reservation.SubmissionId.Value,
                emailResult.UserEmailSent,
                emailResult.AdminEmailSent);
        }

        if (!emailResult.Success)
        {
            _logger.LogWarning(
                "Contact request email send failed for user {UserId}. UserEmailSent={UserEmailSent}, AdminEmailSent={AdminEmailSent}",
                userId,
                emailResult.UserEmailSent,
                emailResult.AdminEmailSent);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "We could not send your message right now. Please try again later." });
        }

        return Ok(new { message = "Your message has been sent." });
    }

    private static string? ValidateRequest(MobileContactRequest? request)
    {
        if (request == null)
        {
            return "Contact request is required.";
        }

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            return "Please select a subject.";
        }

        if (!ContactRequestSubjectTypes.All.Contains(request.Subject.Trim(), StringComparer.Ordinal))
        {
            return "Please select a valid subject.";
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return "Please enter a message.";
        }

        if (request.Message.Trim().Length > MaxMessageLength)
        {
            return $"Please keep your message under {MaxMessageLength} characters.";
        }

        return null;
    }

    private bool TryGetCurrentUser(out int userId, out string userEmail)
    {
        userId = 0;
        userEmail = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return int.TryParse(userIdValue, out userId) && !string.IsNullOrWhiteSpace(userEmail);
    }

    private string? ResolveClientIpAddress()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}

public class MobileContactRequest
{
    public string Subject { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}