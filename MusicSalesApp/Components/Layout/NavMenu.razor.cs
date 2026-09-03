#nullable enable

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
    private const string TestEnvironmentName = "Test";
    protected const string TestingServerBannerText = "This is the Streamtunes Testing Server. The Production server URL is";
    protected const string ProductionServerUrl = "https://streamtunes.net";

    protected bool _isMenuOpen = false;
    protected bool _isDarkTheme = false;
    protected SfSidebar _sidebar = default!;
    protected string? _appVersion;
    protected bool ShowTestingServerBanner => Environment.IsEnvironment(TestEnvironmentName);

    // Site maintenance notification state
    protected bool _showMaintenanceWarning = false;
    protected bool _showMaintenanceDialog = false;
    protected string _maintenanceStartLocal = string.Empty;
    protected string _maintenanceEndLocal = string.Empty;
    protected string _maintenanceTzAbbr = string.Empty;

    // Track the current maintenance window for acknowledgment key
    private DateTime? _currentStartUtc;
    private DateTime? _currentEndUtc;

    // Timer that fires when the scheduled maintenance window expires so the banner/dialog
    // close automatically for long-lived sessions without waiting for the next Hangfire run.
    private ITimer? _maintenanceExpiryTimer;

    // System.Threading.Timer's dueTime has an upper bound of uint.MaxValue - 1 milliseconds
    // (~49.7 days). When the maintenance window's end exceeds that, skip scheduling the
    // client-side timer and rely on the SignalR/Hangfire mechanism instead.
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds((double)uint.MaxValue - 1);

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

            try
            {
                await MaintenanceHubClient.StartAsync();
                await InitializeTheme();
                await LoadMaintenanceNoticeAsync();
                _appVersion = await AppSettingsService.GetAppVersionAsync();
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
            {
                // Four awaits in a row, on every page of the site, immediately after the first
                // render - the window in which a visitor clicking straight through is most likely
                // to leave. Note the ErrorBoundary in NavMenu.razor cannot catch this: the runtime
                // looks for a boundary among the throwing component's *ancestors*, and that
                // boundary is this component's child. Unguarded, this reached CircuitHost and
                // destroyed the circuit on 2026-09-02 21:42.
                Logger.LogDebug(ex, "Nav menu initialisation stopped because the circuit was going away.");
            }
        }
    }

    private void HandleMaintenanceUpdated() => DispatchUiUpdate(LoadMaintenanceNoticeAsync);

    private async Task LoadMaintenanceNoticeAsync()
    {
        try
        {
            if (!await AppSettingsService.ShouldShowSiteMaintenanceNoticeAsync())
            {
                _showMaintenanceWarning = false;
                _showMaintenanceDialog = false;
                CancelMaintenanceExpiryTimer();
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

            // Schedule a client-side timer so the banner/dialog close automatically when the
            // window expires, without relying solely on the periodic Hangfire reset job.
            ScheduleMaintenanceExpiryTimer(endUtc);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load site maintenance notice");
        }
    }

    /// <summary>
    /// Schedules a one-shot timer that re-evaluates the maintenance notice when the window ends.
    /// This ensures users on long-lived sessions see the banner/dialog dismissed at the exact end
    /// time rather than waiting up to an hour for the Hangfire reset job to trigger a SignalR push.
    /// </summary>
    private void ScheduleMaintenanceExpiryTimer(DateTime? endUtc)
    {
        CancelMaintenanceExpiryTimer();

        if (!endUtc.HasValue || endUtc.Value == DateTime.MinValue)
            return;

        var delay = endUtc.Value - TimeProvider.GetUtcNow().UtcDateTime;
        if (delay <= TimeSpan.Zero)
        {
            // Already past the end – force an immediate re-check.
            HandleMaintenanceUpdated();
            return;
        }

        // System.Threading.Timer has an upper bound on dueTime (~49.7 days).
        // For end times beyond that, skip the client-side timer and rely on SignalR/Hangfire.
        if (delay > MaxTimerDelay)
            return;

        _maintenanceExpiryTimer = TimeProvider.CreateTimer(_ =>
        {
            if (_disposed) return;

            // The disposal race between that check and the hop is handled inside DispatchUiUpdate.
            DispatchUiUpdate(LoadMaintenanceNoticeAsync);
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    private void CancelMaintenanceExpiryTimer()
    {
        _maintenanceExpiryTimer?.Dispose();
        _maintenanceExpiryTimer = null;
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

    protected void CloseMenu(Syncfusion.Blazor.Navigations.MenuEventArgs<MenuItem> args)
    {
        _isMenuOpen = false;

        // Logout must be a full page load to properly clear the auth cookie
        if (args.Item?.Url == "/logout")
        {
            NavigationManager.NavigateTo("/account/logout", forceLoad: true);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            ThemeService.OnThemeChanged -= HandleThemeChanged;
            MaintenanceHubClient.OnMaintenanceUpdated -= HandleMaintenanceUpdated;
            CancelMaintenanceExpiryTimer();
            _disposed = true;
        }
        return ValueTask.CompletedTask;
    }
}
