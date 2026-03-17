#nullable enable

using Microsoft.AspNetCore.SignalR;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Hubs;

namespace MusicSalesApp.Services;

public class MaintenanceResetService : IMaintenanceResetService
{
    private readonly IAppSettingsService _appSettingsService;
    private readonly IHubContext<MaintenanceHub> _hubContext;
    private readonly ILogger<MaintenanceResetService> _logger;

    public MaintenanceResetService(
        IAppSettingsService appSettingsService,
        IHubContext<MaintenanceHub> hubContext,
        ILogger<MaintenanceResetService> logger)
    {
        _appSettingsService = appSettingsService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task ResetExpiredMaintenanceWindowsAsync()
    {
        var now = DateTime.UtcNow;

        // Reset expired site maintenance window
        var siteEnd = await _appSettingsService.GetSiteMaintenanceEndUtcAsync();
        if (siteEnd.HasValue && siteEnd.Value != DateTime.MinValue && siteEnd.Value < now)
        {
            _logger.LogInformation("Site maintenance window has expired (ended {EndUtc}). Resetting to DateTime.MinValue.", siteEnd.Value);
            await _appSettingsService.SetSiteMaintenanceStartUtcAsync(DateTime.MinValue);
            await _appSettingsService.SetSiteMaintenanceEndUtcAsync(DateTime.MinValue);
            await _hubContext.Clients.All.SendAsync(SignalRMethodNames.ReceiveMaintenanceUpdate);
        }

        // Reset expired Tax Bandits maintenance window
        var taxEnd = await _appSettingsService.GetTaxBanditsMaintenanceEndUtcAsync();
        if (taxEnd.HasValue && taxEnd.Value != DateTime.MinValue && taxEnd.Value < now)
        {
            _logger.LogInformation("Tax Bandits maintenance window has expired (ended {EndUtc}). Resetting to DateTime.MinValue.", taxEnd.Value);
            await _appSettingsService.SetTaxBanditsMaintenanceStartUtcAsync(DateTime.MinValue);
            await _appSettingsService.SetTaxBanditsMaintenanceEndUtcAsync(DateTime.MinValue);
        }
    }
}
