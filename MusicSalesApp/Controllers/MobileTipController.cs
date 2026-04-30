using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Middleware;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

[Route("api/mobile/tips")]
[ApiController]
[RequireMobileApiKey]
[Authorize(Policy = Permissions.ValidatedUser)]
public class MobileTipController : ControllerBase
{
    private const string DefaultMobileTipCallbackUrl = "streamtunes://tip";

    private readonly ITipService _tipService;
    private readonly IConfiguration _configuration;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public MobileTipController(
        ITipService tipService,
        IConfiguration configuration,
        IDbContextFactory<AppDbContext> contextFactory)
    {
        _tipService = tipService;
        _configuration = configuration;
        _contextFactory = contextFactory;
    }

    [HttpPost("create-order")]
    public async Task<IActionResult> CreateOrder([FromBody] MobileCreateTipRequest request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        if (request == null || request.CreatorId <= 0)
            return BadRequest(new { message = "CreatorId is required." });

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var callbackUrl = GetMobileTipCallbackUrl();

        var (success, errorMessage, approvalUrl) = await _tipService.CreateTipOrderAsync(
            userId,
            request.CreatorId,
            request.SongMetadataId,
            request.Amount,
            ipAddress,
            request.DeviceFingerprint,
            callbackUrl);

        if (success && !string.IsNullOrWhiteSpace(approvalUrl))
        {
            return Ok(new MobileTipOperationResponse
            {
                Success = true,
                ResultKind = MobileTipResultKinds.RequiresApproval,
                ApprovalUrl = approvalUrl
            });
        }

        return Ok(new MobileTipOperationResponse
        {
            Success = false,
            ResultKind = ClassifyFailureKind(errorMessage),
            Message = errorMessage ?? "Unable to create the tip payment. Please try again."
        });
    }

    [HttpPost("capture")]
    public async Task<IActionResult> Capture([FromBody] MobileTipOrderRequest request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        if (request == null || string.IsNullOrWhiteSpace(request.PayPalOrderId))
            return BadRequest(new { message = "PayPalOrderId is required." });

        if (!await UserOwnsPendingTipAsync(userId, request.PayPalOrderId))
            return NotFound(new { message = "Tip not found." });

        var (success, errorMessage, amount) = await _tipService.CaptureTipAsync(request.PayPalOrderId);
        if (success)
        {
            return Ok(new MobileTipOperationResponse
            {
                Success = true,
                ResultKind = MobileTipResultKinds.Succeeded,
                Amount = amount,
                Message = $"Your ${amount:F2} tip was sent successfully! Thank you for supporting this creator."
            });
        }

        return Ok(new MobileTipOperationResponse
        {
            Success = false,
            ResultKind = ClassifyFailureKind(errorMessage),
            Message = errorMessage ?? "Failed to process your tip. Please try again."
        });
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel([FromBody] MobileTipOrderRequest request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        if (request == null || string.IsNullOrWhiteSpace(request.PayPalOrderId))
            return BadRequest(new { message = "PayPalOrderId is required." });

        if (!await UserOwnsPendingTipAsync(userId, request.PayPalOrderId))
            return NotFound(new { message = "Tip not found." });

        await _tipService.CancelTipAsync(request.PayPalOrderId);

        return Ok(new MobileTipOperationResponse
        {
            Success = true,
            ResultKind = MobileTipResultKinds.Cancelled,
            Message = "Tip payment was cancelled."
        });
    }

    private bool TryGetUserId(out int userId)
    {
        userId = 0;
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out userId);
    }

    private string GetMobileTipCallbackUrl()
    {
        return _configuration[AppSettingKeys.MobileTipCallbackUrl] ?? DefaultMobileTipCallbackUrl;
    }

    private async Task<bool> UserOwnsPendingTipAsync(int userId, string payPalOrderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Tips.AnyAsync(t =>
            t.PayPalOrderId == payPalOrderId &&
            t.TipperUserId == userId &&
            t.Status == TipStatus.Pending);
    }

    private static string ClassifyFailureKind(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return MobileTipResultKinds.PaymentFailure;

        if (IsFraudPreventedMessage(errorMessage))
            return MobileTipResultKinds.FraudPrevented;

        if (IsValidationBlockedMessage(errorMessage))
            return MobileTipResultKinds.ValidationBlocked;

        return MobileTipResultKinds.PaymentFailure;
    }

    private static bool IsFraudPreventedMessage(string errorMessage)
    {
        return errorMessage.Contains("Unusual activity detected", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("Reciprocal tipping limit reached", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("tipping privileges have been revoked", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidationBlockedMessage(string errorMessage)
    {
        return errorMessage.Contains("Minimum tip amount", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("Maximum tip amount", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("Your account must be at least", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("You cannot tip yourself", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("You can send a maximum of", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("You have reached the maximum number of tips to this creator", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("Creator not found", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("User not found", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("Return URL is required", StringComparison.OrdinalIgnoreCase);
    }
}

public class MobileCreateTipRequest
{
    public int CreatorId { get; set; }
    public int? SongMetadataId { get; set; }
    public decimal Amount { get; set; }
    public string DeviceFingerprint { get; set; }
}

public class MobileTipOrderRequest
{
    public string PayPalOrderId { get; set; } = string.Empty;
}

public class MobileTipOperationResponse
{
    public bool Success { get; set; }
    public string ResultKind { get; set; } = MobileTipResultKinds.PaymentFailure;
    public string Message { get; set; }
    public string ApprovalUrl { get; set; }
    public decimal? Amount { get; set; }
}