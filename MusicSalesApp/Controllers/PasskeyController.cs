using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Security.Claims;
using System.Text.Json;

namespace MusicSalesApp.Controllers;

[Route("api/[controller]")]
[ApiController]
public class PasskeyController : ControllerBase
{
    private readonly IPasskeyService _passkeyService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILogger<PasskeyController> _logger;

    // In-memory storage for options (in production, use distributed cache)
    private static readonly Dictionary<string, CredentialCreateOptions> _credentialCreateOptionsCache = new();
    private static readonly Dictionary<string, AssertionOptions> _assertionOptionsCache = new();

    public PasskeyController(
        IPasskeyService passkeyService,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<PasskeyController> logger)
    {
        _passkeyService = passkeyService;
        _userManager = userManager;
        _signInManager = signInManager;
        _logger = logger;
    }

    [Authorize]
    [HttpPost("register/begin")]
    public async Task<IActionResult> BeginRegistration([FromBody] BeginRegistrationRequest request)
    {
        try
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var options = await _passkeyService.BeginRegistrationAsync(userId, request.PasskeyName);
            
            // Store options in cache (in production, use distributed cache with user session)
            var sessionId = Guid.NewGuid().ToString();
            _credentialCreateOptionsCache[sessionId] = options;
            HttpContext.Response.Cookies.Append("passkey_session", sessionId, new CookieOptions 
            { 
                HttpOnly = true, 
                Secure = true, 
                SameSite = SameSiteMode.Strict,
                MaxAge = TimeSpan.FromMinutes(5)
            });

            return Ok(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error beginning passkey registration");
            return BadRequest(new { message = "Failed to begin passkey registration" });
        }
    }

    [Authorize]
    [HttpPost("register/complete")]
    public async Task<IActionResult> CompleteRegistration([FromBody] CompleteRegistrationRequest request)
    {
        try
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            
            var success = await _passkeyService.CompleteRegistrationAsync(
                userId, 
                request.PasskeyName, 
                request.AttestationResponse);

            if (success)
            {
                return Ok(new { message = "Passkey registered successfully" });
            }
            else
            {
                return BadRequest(new { message = "Failed to register passkey" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey registration");
            return BadRequest(new { message = "Failed to register passkey" });
        }
    }

    [HttpPost("login/begin")]
    public async Task<IActionResult> BeginLogin([FromBody] BeginLoginRequest request)
    {
        try
        {
            var options = await _passkeyService.BeginLoginAsync(request.Username);
            
            // Store options in cache
            var sessionId = Guid.NewGuid().ToString();
            _assertionOptionsCache[sessionId] = options;
            HttpContext.Response.Cookies.Append("passkey_login_session", sessionId, new CookieOptions 
            { 
                HttpOnly = true, 
                Secure = true, 
                SameSite = SameSiteMode.Strict,
                MaxAge = TimeSpan.FromMinutes(5)
            });

            return Ok(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error beginning passkey login");
            return BadRequest(new { message = "Failed to begin passkey login" });
        }
    }

    [HttpPost("login/complete")]
    public async Task<IActionResult> CompleteLogin([FromBody] AuthenticatorAssertionRawResponse assertionResponse)
    {
        try
        {
            var user = await _passkeyService.CompleteLoginAsync(assertionResponse);
            
            if (user != null)
            {
                // Check if account is suspended
                if (user.IsSuspended)
                {
                    return Unauthorized(new { message = "Your account has been suspended. Please contact support to reactivate your account." });
                }

                // Sign in the user
                await _signInManager.SignInAsync(user, isPersistent: true);
                return Ok(new { message = "Login successful" });
            }
            else
            {
                return Unauthorized(new { message = "Invalid passkey" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey login");
            return Unauthorized(new { message = "Invalid passkey" });
        }
    }

    [Authorize]
    [HttpGet("list")]
    public async Task<IActionResult> ListPasskeys()
    {
        try
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var passkeys = await _passkeyService.GetUserPasskeysAsync(userId);
            
            var result = passkeys.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                createdAt = p.CreatedAt,
                lastUsedAt = p.LastUsedAt
            });

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing passkeys");
            return BadRequest(new { message = "Failed to list passkeys" });
        }
    }

    [Authorize]
    [HttpDelete("{passkeyId}")]
    public async Task<IActionResult> DeletePasskey(int passkeyId)
    {
        try
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var success = await _passkeyService.DeletePasskeyAsync(userId, passkeyId);
            
            if (success)
            {
                return Ok(new { message = "Passkey deleted successfully" });
            }
            else
            {
                return NotFound(new { message = "Passkey not found" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting passkey");
            return BadRequest(new { message = "Failed to delete passkey" });
        }
    }

    [Authorize]
    [HttpPut("{passkeyId}/rename")]
    public async Task<IActionResult> RenamePasskey(int passkeyId, [FromBody] RenamePasskeyRequest request)
    {
        try
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var success = await _passkeyService.RenamePasskeyAsync(userId, passkeyId, request.NewName);
            
            if (success)
            {
                return Ok(new { message = "Passkey renamed successfully" });
            }
            else
            {
                return NotFound(new { message = "Passkey not found" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming passkey");
            return BadRequest(new { message = "Failed to rename passkey" });
        }
    }
}

public class BeginRegistrationRequest
{
    public string PasskeyName { get; set; } = string.Empty;
}

public class CompleteRegistrationRequest
{
    public string PasskeyName { get; set; } = string.Empty;
    public AuthenticatorAttestationRawResponse AttestationResponse { get; set; }
}

public class BeginLoginRequest
{
    public string Username { get; set; } = string.Empty;
}

public class RenamePasskeyRequest
{
    public string NewName { get; set; } = string.Empty;
}
