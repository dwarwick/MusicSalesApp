using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Middleware;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

[Route("api/mobile/admin-messages")]
[ApiController]
[RequireMobileApiKey]
[Authorize]
public class MobileAdminMessageController : ControllerBase
{
    private readonly IAdminMessageService _adminMessageService;

    public MobileAdminMessageController(IAdminMessageService adminMessageService)
    {
        _adminMessageService = adminMessageService;
    }

    [HttpGet("pending-dialogs")]
    public async Task<IActionResult> GetPendingDialogs()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var messages = await _adminMessageService.GetPendingDialogMessagesAsync(userId);
        return Ok(messages);
    }

    [HttpPost("{messageId:int}/acknowledge")]
    public async Task<IActionResult> Acknowledge(int messageId)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var acknowledged = await _adminMessageService.AcknowledgeMessageAsync(userId, messageId);
        if (!acknowledged)
        {
            return NotFound();
        }

        return Ok();
    }

    private bool TryGetUserId(out int userId)
    {
        userId = 0;
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out userId);
    }
}