#nullable enable
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Middleware;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// Device registration for push notifications.
/// </summary>
/// <remarks>
/// Registration is idempotent and safe to repeat: the client calls it on every launch and on every
/// auth change, because a token can rotate at any time and the only way to notice is to re-register.
/// </remarks>
[Route("api/mobile/push")]
[ApiController]
[RequireMobileApiKey]
[Authorize]
public class MobilePushController : ControllerBase
{
    private readonly IPushDeviceTokenService _deviceTokenService;

    public MobilePushController(IPushDeviceTokenService deviceTokenService)
    {
        _deviceTokenService = deviceTokenService;
    }

    /// <summary>
    /// Registers, or re-registers, this device for the calling user.
    /// </summary>
    [HttpPut("devices")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterPushDeviceRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { error = "A device token is required." });
        }

        if (PushPlatforms.Normalize(request.Platform) is null)
        {
            return BadRequest(new { error = "Unsupported platform." });
        }

        var registered = await _deviceTokenService.RegisterAsync(
            userId, request.Platform!, request.Token, request.DeviceId);

        // 400 rather than 500 on refusal, for the same reason the follow routes do: the client
        // retries a 5xx forever, and an over-long or malformed token is never going to succeed.
        return registered
            ? Ok()
            : BadRequest(new { error = "The device could not be registered." });
    }

    /// <summary>
    /// Retires this device, for a sign-out or an in-app opt-out.
    /// </summary>
    [HttpDelete("devices")]
    public async Task<IActionResult> UnregisterDevice([FromBody] UnregisterPushDeviceRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { error = "A device token is required." });
        }

        // A token that is already gone is not an error - a client signing out twice, or replaying a
        // queued unregister, has got what it wanted either way.
        await _deviceTokenService.UnregisterAsync(userId, request.Token);
        return Ok();
    }

    private bool TryGetUserId(out int userId)
    {
        userId = 0;
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out userId);
    }
}

public sealed class RegisterPushDeviceRequest
{
    /// <summary>One of <c>PushPlatforms</c>.</summary>
    public string? Platform { get; set; }

    /// <summary>The FCM registration token, or the APNs device token as hex.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// A stable per-install id, when the client has one. Lets a rotated token replace its
    /// predecessor rather than leaving a dead row behind.
    /// </summary>
    public string? DeviceId { get; set; }
}

public sealed class UnregisterPushDeviceRequest
{
    public string Token { get; set; } = string.Empty;
}
