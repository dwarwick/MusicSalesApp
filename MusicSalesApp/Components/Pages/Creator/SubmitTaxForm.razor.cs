#nullable enable
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages.Creator;

public partial class SubmitTaxFormModel : BlazorBase
{
    protected bool _loading = true;
    protected bool _isCooldownActive = false;
    protected string _errorMessage = string.Empty;
    private bool _hasLoadedData = false;
    private IJSObjectReference? _jsModule;
    protected bool _isMaintenanceActive = false;
    protected DateTime? _maintenanceStartUtc = null;
    protected DateTime? _maintenanceEndUtc = null;
    protected string _maintenanceStartLocal = string.Empty;
    protected string _maintenanceEndLocal = string.Empty;
    protected string _maintenanceTimeZoneAbbreviation = string.Empty;
    private ApplicationUser? _currentUser = null;
    private bool _taxFormLoadedTracked = false;
    private bool _taxFormReturnedTracked = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated != true)
                {
                    NavigationManager.NavigateTo(AppPageRoutes.Login);
                    return;
                }

                _currentUser = await UserManager.GetUserAsync(user);
                if (_currentUser == null)
                {
                    NavigationManager.NavigateTo(AppPageRoutes.Login);
                    return;
                }

                // Check if Tax Bandits is currently in a maintenance window
                if (await AppSettingsService.IsTaxBanditsMaintenanceActiveAsync())
                {
                    _isMaintenanceActive = true;
                    _maintenanceStartUtc = await AppSettingsService.GetTaxBanditsMaintenanceStartUtcAsync();
                    _maintenanceEndUtc = await AppSettingsService.GetTaxBanditsMaintenanceEndUtcAsync();

                    // Get user's local time zone info via JS interop
                    var localInfo = await TimeZoneHelper.GetUserLocalTimeAsync(JS, _maintenanceStartUtc, _maintenanceEndUtc);
                    _maintenanceStartLocal = localInfo.StartLocal;
                    _maintenanceEndLocal = localInfo.EndLocal;
                    _maintenanceTimeZoneAbbreviation = localInfo.TimeZoneAbbreviation;

                    _loading = false;
                    await InvokeAsync(StateHasChanged);
                    return;
                }

                // Check if user is in a 24-hour TIN match cooldown period
                var userIdInt = GetUserId(user);
                if (userIdInt.HasValue)
                {
                    var creator = await CreatorService.GetCreatorByUserIdAsync(userIdInt.Value);
                    if (creator?.LastTinMatchFailedAt != null)
                    {
                        var cooldownEnd = creator.LastTinMatchFailedAt.Value.AddHours(24);
                        if (DateTime.UtcNow < cooldownEnd)
                        {
                            _isCooldownActive = true;
                            _loading = false;
                            await InvokeAsync(StateHasChanged);
                            return;
                        }
                    }
                }

                // Get the transient token and configuration from the server
                Logger.LogInformation("Fetching tax form token from API");
                var response = await Http.GetFromJsonAsync<TaxFormTokenResponse>("api/creator/tax-form-token");

                if (response == null || !response.Success)
                {
                    Logger.LogWarning("Tax form token request failed: {Error}", response?.ErrorMessage ?? "null response");
                    _errorMessage = response?.ErrorMessage ?? "Failed to load tax form. Please return to Creator / Artist Settings and try again.";
                    _loading = false;
                    await InvokeAsync(StateHasChanged);
                    return;
                }

                Logger.LogInformation("Tax form token received successfully. BusinessId: {BusinessId}",
                    response.BusinessId);

                await TrackTaxFormLoadedAsync();

                _loading = false;
                await InvokeAsync(StateHasChanged);

                // Load JS module and initialize the Drop-in UI
                _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Creator/SubmitTaxForm.razor.js");
                
                var baseUrl = NavigationManager.BaseUri.TrimEnd('/');
                Logger.LogInformation("Initializing TaxBandits Drop-in UI with return URL: {ReturnUrl}", $"{baseUrl}/CreatorSettings");
                await _jsModule.InvokeVoidAsync("initTaxForm",
                    response.TransientToken,
                    response.PayeeRef,
                    response.BusinessId,
                    response.ScriptUrl,
                    $"{baseUrl}/CreatorSettings",
                    DotNetObjectReference.Create(this));
            }
            catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
            {
                // The visitor left, or the circuit dropped, while this was still awaiting.
                // Nothing is wrong and there is nobody to tell, so it must not reach the
                // Error sink - that is what emailed the admin five times on 2026-09-02.
                Logger.LogDebug(ex, "Error loading tax form");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading tax form");
                _errorMessage = "An error occurred while loading the tax form. Please return to Creator / Artist Settings and try again.";
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    /// <summary>
    /// Called from JavaScript when the tax form submission completes or is cancelled.
    /// </summary>
    [JSInvokable]
    public async Task OnTaxFormComplete(string status)
    {
        Logger.LogInformation("Tax form completed with status: {Status}", status);
        await TrackTaxFormReturnedAsync(status);
        NavigationManager.NavigateTo(AppPageRoutes.CreatorSettings, forceLoad: true);
    }

    private async Task TrackTaxFormLoadedAsync()
    {
        if (_taxFormLoadedTracked || _currentUser == null)
        {
            return;
        }

        _taxFormLoadedTracked = true;

        await TrackTaxFormFunnelEventAsync(
            FunnelAnalyticsEvents.CreatorTaxFormLoaded,
            FunnelAnalyticsLabels.TaxFormLoaded,
            null);

        await RecordTaxFormHistoryAsync(
            UserHistoryEventTypes.CreatorTaxFormLoaded,
            "Creator tax form loaded through TaxBandits.");
    }

    private async Task TrackTaxFormReturnedAsync(string status)
    {
        if (_taxFormReturnedTracked || _currentUser == null)
        {
            return;
        }

        _taxFormReturnedTracked = true;

        await TrackTaxFormFunnelEventAsync(
            FunnelAnalyticsEvents.CreatorTaxFormCompletedOrReturned,
            FunnelAnalyticsLabels.TaxFormReturned,
            status);

        await RecordTaxFormHistoryAsync(
            UserHistoryEventTypes.CreatorTaxFormCompletedOrReturned,
            $"Creator tax form completed or returned with status: {status}.");
    }

    private async Task TrackTaxFormFunnelEventAsync(string eventName, string label, string? taxFormStatus)
    {
        var payload = new Dictionary<string, object>
        {
            [FunnelAnalyticsParameters.Category] = FunnelAnalyticsLabels.CreatorCategory,
            [FunnelAnalyticsParameters.Label] = label,
            [FunnelAnalyticsParameters.Source] = FunnelAnalyticsLabels.TaxBandits
        };

        if (!string.IsNullOrWhiteSpace(taxFormStatus))
        {
            payload[FunnelAnalyticsParameters.TaxFormStatus] = taxFormStatus;
        }

        try
        {
            await JS.InvokeVoidAsync(
                GoogleAdsTrackingConfigKeys.TrackFunnelEventFunctionName,
                eventName,
                payload);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send tax form funnel analytics event {EventName}", eventName);
        }
    }

    private async Task RecordTaxFormHistoryAsync(string eventType, string description)
    {
        try
        {
            await AdminNotificationService.RecordUserHistoryAsync(
                _currentUser!.Id,
                _currentUser.Email ?? string.Empty,
                eventType,
                description);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to record tax form funnel history event {EventType}", eventType);
        }
    }
}

public class TaxFormTokenResponse
{
    public bool Success { get; set; }
    public string? TransientToken { get; set; }
    public string? PayeeRef { get; set; }
    public string? BusinessId { get; set; }
    public string? ScriptUrl { get; set; }
    public string? ErrorMessage { get; set; }
}
