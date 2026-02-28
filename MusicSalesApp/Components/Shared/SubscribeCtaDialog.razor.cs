using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Shared;

public partial class SubscribeCtaDialogModel : BlazorBase
{
    private const int MinPreviewInterval = 2;
    private const int MaxPreviewIntervalExclusive = 5; // Random.Next upper bound is exclusive, so this gives 2-4

    protected bool _showDialog;
    private int _previewCount;
    private int _nextShowAtCount;
    private bool _initialized;
    protected string _subscriptionPrice = "3.99";
    private bool _priceLoaded;

    [Parameter]
    public bool IsAuthenticated { get; set; }

    [Parameter]
    public bool HasActiveSubscription { get; set; }

    /// <summary>
    /// Called when the user finishes a restricted preview.
    /// Tracks preview count and shows the CTA dialog at the appropriate interval.
    /// Shows on the first preview, then every 2-4 previews after that.
    /// </summary>
    public async Task OnPreviewEndedAsync()
    {
        // Don't show for subscribers or admins
        if (HasActiveSubscription)
            return;

        _previewCount++;

        if (!_initialized)
        {
            // Show on first preview
            _initialized = true;
            _nextShowAtCount = _previewCount;
        }

        if (_previewCount >= _nextShowAtCount)
        {
            // Set next show count (2-4 previews from now)
            _nextShowAtCount = _previewCount + Random.Shared.Next(MinPreviewInterval, MaxPreviewIntervalExclusive);
            await ShowDialogAsync();
        }
    }

    /// <summary>
    /// Resets the preview counter. Should be called when the user navigates to the page.
    /// </summary>
    public void ResetCounter()
    {
        _previewCount = 0;
        _nextShowAtCount = 0;
        _initialized = false;
    }

    private async Task ShowDialogAsync()
    {
        if (!_priceLoaded)
        {
            await LoadSubscriptionPriceAsync();
        }

        _showDialog = true;
        await InvokeAsync(StateHasChanged);
    }

    protected void CloseDialog()
    {
        _showDialog = false;
    }

    protected void NavigateToLogin()
    {
        _showDialog = false;
        NavigationManager.NavigateTo("/login?returnUrl=" + Uri.EscapeDataString(NavigationManager.Uri), forceLoad: true);
    }

    protected void NavigateToRegister()
    {
        _showDialog = false;
        NavigationManager.NavigateTo("/register", forceLoad: true);
    }

    protected void NavigateToSubscribe()
    {
        _showDialog = false;
        NavigationManager.NavigateTo("/manage-account", forceLoad: true);
    }

    private async Task LoadSubscriptionPriceAsync()
    {
        try
        {
            var price = await AppSettingsService.GetSubscriptionPriceAsync();
            _subscriptionPrice = price.ToString("F2");
            _priceLoaded = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load subscription price.");
        }
    }
}
