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
        var subscriptionPrice = await _appSettingsService.GetSubscriptionPriceAsync();
        var streamQualifyingSeconds = await _appSettingsService.GetStreamQualifyingSecondsAsync();

        return Ok(new
        {
            subscriptionPrice = subscriptionPrice.ToString("F2"),
            streamQualifyingSeconds
        });
    }
}
