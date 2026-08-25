using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Middleware;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

[Route("api/mobile-settings")]
[ApiController]
[RequireMobileApiKey]
public class MobileSettingsController : ControllerBase
{
    private readonly IAppSettingsService _appSettingsService;

    public MobileSettingsController(IAppSettingsService appSettingsService)
    {
        _appSettingsService = appSettingsService;
    }

    /// <summary>
    /// Returns all settings relevant to the mobile app in a single response.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetMobileSettings()
    {
        // The promotional reduction is applied here rather than in the app, so a mobile release does not
        // have to ship for the flag to take effect. The per-song value the app prefers over this one is
        // reduced the same way in MobileSongMapper.
        var streamQualifying = await _appSettingsService.GetStreamQualifyingSettingsAsync();
        var streamQualifyingSeconds = streamQualifying.Resolve(creatorSeconds: null);

        return Ok(new
        {
            streamQualifyingSeconds
        });
    }
}
