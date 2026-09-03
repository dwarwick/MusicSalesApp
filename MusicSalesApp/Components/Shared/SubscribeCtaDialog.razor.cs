using Microsoft.AspNetCore.Components;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Popups;
using System.Globalization;

#nullable enable

namespace MusicSalesApp.Components.Shared;

public partial class SubscribeCtaDialogModel : BlazorBase
{
    private const int MinPreviewInterval = 2;
    private const int MaxPreviewIntervalExclusive = 5; // Random.Next upper bound is exclusive, so this gives 2-4

    protected bool _showDialog;
    private int _previewCount;
    private int _nextShowAtCount;
    private bool _initialized;
    protected PayPalWebOfferQuote? _offerQuote;
    private bool _offerLoaded;

    [Parameter]
    public bool IsAuthenticated { get; set; }

    [Parameter]
    public bool HasActiveSubscription { get; set; }

    protected bool HasFreeTrialOffer => _offerQuote?.HasFreeTrial == true;
    protected string TrialDurationDisplay => _offerQuote?.TrialDays is int trialDays
        ? $"{trialDays} {(trialDays == 1 ? "day" : "days")}"
        : string.Empty;
    protected string TrialHeadlineDisplay => _offerQuote?.TrialDays is int trialDays
        ? $"{trialDays}-Day"
        : string.Empty;
    protected string OfferPriceDisplay => FormatOfferPrice(_offerQuote);
    protected string OfferCadenceDisplay => FormatOfferCadence(_offerQuote);
    protected string DialogTitle => HasFreeTrialOffer
        ? $"Start Your {TrialHeadlineDisplay} Free Trial"
        : "Unlimited Music Streaming";
    protected string RegisterButtonLabel => HasFreeTrialOffer ? "Register for Free Trial" : "Register";
    protected string SubscribeButtonLabel => HasFreeTrialOffer ? "Start My Free Trial" : "Subscribe";

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

            if (!_offerLoaded)
            {
                await LoadOfferQuoteAsync();
            }

            _showDialog = true;
            await InvokeAsync(StateHasChanged);
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

    /// <summary>
    /// Handles the dialog's OnClose event.
    /// In Syncfusion, OnClose fires with BeforeCloseEventArgs before the dialog fully closes.
    /// Sets _showDialog to false immediately to prevent race conditions
    /// where a parent re-render could re-open the dialog.
    /// </summary>
    protected void OnDialogClosing(BeforeCloseEventArgs args)
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

    private async Task LoadOfferQuoteAsync()
    {
        try
        {
            int? userId = null;
            if (IsAuthenticated)
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var appUser = await UserManager.GetUserAsync(authState.User);
                if (appUser == null)
                {
                    _offerLoaded = true;
                    return;
                }

                userId = appUser.Id;
            }

            _offerQuote = await PayPalSubscriptionManagementService.GetOfferQuoteAsync(userId);
        }
        catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
        {
            // The visitor left, or the circuit dropped, while this was still awaiting.
            // Nothing is wrong and there is nobody to tell, so it must not reach the
            // Error sink - that is what emailed the admin five times on 2026-09-02.
            Logger.LogDebug(ex, "Failed to load the PayPal web subscription offer.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the PayPal web subscription offer.");
        }
        finally
        {
            _offerLoaded = true;
        }
    }

    private static string FormatOfferPrice(PayPalWebOfferQuote? offer)
    {
        if (offer == null)
        {
            return string.Empty;
        }

        var amount = offer.RegularPrice.ToString("0.00", CultureInfo.InvariantCulture);
        return string.Equals(
            offer.CurrencyCode,
            PayPalSubscriptionDefaults.UsdCurrencyCode,
            StringComparison.OrdinalIgnoreCase)
            ? $"${amount}"
            : $"{amount} {offer.CurrencyCode.ToUpperInvariant()}";
    }

    private static string FormatOfferCadence(PayPalWebOfferQuote? offer)
    {
        if (offer == null)
        {
            return string.Empty;
        }

        var intervalCount = Math.Max(offer.IntervalCount, 1);
        var intervalUnit = offer.IntervalUnit.Trim().ToLowerInvariant();
        return intervalCount == 1
            ? $"per {intervalUnit}"
            : $"every {intervalCount} {intervalUnit}s";
    }
}
