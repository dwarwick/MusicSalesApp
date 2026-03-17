using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Buttons;
using Syncfusion.Blazor.Navigations;

namespace MusicSalesApp.Components.Layout;

public class NavMenuModel : BlazorBase, IAsyncDisposable
{
    protected bool _isMenuOpen = false;
    protected bool _isDarkTheme = false;
    protected SfSidebar _sidebar;

    // Site maintenance notification state
    protected bool _showMaintenanceWarning = false;
    protected bool _showMaintenanceDialog = false;
    protected string _maintenanceStartLocal = string.Empty;
    protected string _maintenanceEndLocal = string.Empty;
    protected string _maintenanceTzAbbr = string.Empty;

    // Track the current maintenance window for acknowledgment key
    private DateTime? _currentStartUtc;
    private DateTime? _currentEndUtc;

    private bool _disposed;
    private bool _hasLoadedData = false;

    protected override void OnInitialized()
    {
        ThemeService.OnThemeChanged += HandleThemeChanged;
        MaintenanceHubClient.OnMaintenanceUpdated += HandleMaintenanceUpdated;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            await MaintenanceHubClient.StartAsync();
            await InitializeTheme();
            await LoadMaintenanceNoticeAsync();
            await InvokeAsync(StateHasChanged);
        }
    }

    private void HandleMaintenanceUpdated()
    {
        InvokeAsync(async () =>
        {
            await LoadMaintenanceNoticeAsync();
            StateHasChanged();
        });
    }

    private async Task LoadMaintenanceNoticeAsync()
    {
        try
        {
            if (!await AppSettingsService.ShouldShowSiteMaintenanceNoticeAsync())
            {
                _showMaintenanceWarning = false;
                _showMaintenanceDialog = false;
                return;
            }

            var startUtc = await AppSettingsService.GetSiteMaintenanceStartUtcAsync();
            var endUtc = await AppSettingsService.GetSiteMaintenanceEndUtcAsync();

            var localInfo = await TimeZoneHelper.GetUserLocalTimeAsync(JS, startUtc, endUtc);
            _maintenanceStartLocal = localInfo.StartLocal;
            _maintenanceEndLocal = localInfo.EndLocal;
            _maintenanceTzAbbr = localInfo.TimeZoneAbbreviation;
            _showMaintenanceWarning = true;

            _currentStartUtc = startUtc;
            _currentEndUtc = endUtc;

            // Build a localStorage key unique to this maintenance window
            var ackKey = $"maintenance_ack_{startUtc?.ToString("O")}_{endUtc?.ToString("O")}";
            var alreadyAcknowledged = await JS.InvokeAsync<bool>("checkMaintenanceAcknowledged", ackKey);
            _showMaintenanceDialog = !alreadyAcknowledged;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load site maintenance notice");
        }
    }

    protected async Task AcknowledgeMaintenance()
    {
        _showMaintenanceDialog = false;
        try
        {
            var ackKey = $"maintenance_ack_{_currentStartUtc?.ToString("O")}_{_currentEndUtc?.ToString("O")}";
            await JS.InvokeVoidAsync("acknowledgeMaintenanceNotice", ackKey);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to persist maintenance acknowledgement");
        }
    }

    private async Task InitializeTheme()
    {
        await ThemeService.InitializeThemeAsync();
        _isDarkTheme = ThemeService.IsDarkTheme;
    }

    protected async Task OnThemeChanged(ChangeEventArgs<bool> args)
    {
        var theme = args.Checked ? "Dark" : "Light";
        await ThemeService.SetThemeAsync(theme);
    }

    private void HandleThemeChanged()
    {
        _isDarkTheme = ThemeService.IsDarkTheme;
        InvokeAsync(StateHasChanged);
    }

    protected void ToggleMenu()
    {
        _isMenuOpen = !_isMenuOpen;
    }

    protected void CloseMenu()
    {
        _isMenuOpen = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            ThemeService.OnThemeChanged -= HandleThemeChanged;
            MaintenanceHubClient.OnMaintenanceUpdated -= HandleMaintenanceUpdated;
            _disposed = true;
        }
    }
}
