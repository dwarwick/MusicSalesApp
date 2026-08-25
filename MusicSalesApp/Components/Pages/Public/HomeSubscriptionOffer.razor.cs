using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using System.Globalization;

#nullable enable

namespace MusicSalesApp.Components.Pages.Public;

public partial class HomeSubscriptionOfferModel : BlazorBase
{
    private bool _hasLoadedData;
    protected bool _audienceLoaded;
    protected PayPalWebOfferQuote? _offerQuote;
    protected bool _isAuthenticated;
    protected bool _isEmailVerified;
    protected bool ShouldShowSubscriptionOffer { get; private set; }

    protected bool HasFreeTrialOffer => _offerQuote?.HasFreeTrial == true;
    protected string TrialDurationDisplay => _offerQuote?.TrialDays is int trialDays
        ? $"{trialDays} {(trialDays == 1 ? "day" : "days")}"
        : string.Empty;
    protected string OfferPriceDisplay => FormatOfferPrice(_offerQuote);
    protected string OfferCadenceDisplay => FormatOfferCadence(_offerQuote);
    protected string OfferRegularTermsDisplay => _offerQuote == null
        ? string.Empty
        : $"{OfferPriceDisplay} {OfferCadenceDisplay}";
    protected string SubscriberRegisterLabel => HasFreeTrialOffer ? "Register for Free Trial" : "Register";
    protected string SubscriberSubscribeLabel => HasFreeTrialOffer ? "Start My Free Trial" : "Subscribe with PayPal";
    protected string TrialHeadlineDisplay => _offerQuote?.TrialDays is int trialDays
        ? $"{trialDays}-Day"
        : string.Empty;
    protected string HeroSubscribeLabel => HasFreeTrialOffer
        ? $"Start Your {TrialHeadlineDisplay} Free Trial"
        : _offerQuote == null
            ? "Subscribe Now"
            : $"Subscribe Now - {OfferRegularTermsDisplay}";
    protected string SubscriberLoginUrl => BuildReturnUrl(AppPageRoutes.Login, AppPageRoutes.ManageAccountSubscription);
    protected string SubscriberRegisterUrl => BuildReturnUrl(AppPageRoutes.Register, AppPageRoutes.ManageAccountSubscription);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _hasLoadedData)
        {
            return;
        }

        _hasLoadedData = true;
        try
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var userId = (int?)null;
            if (authState.User.Identity?.IsAuthenticated == true)
            {
                _isAuthenticated = true;
                // Claims, not UserManager. GetUserAsync and IsInRoleAsync are both DB round-trips
                // on the circuit's single scoped AppDbContext, and this island first-renders
                // concurrently with HomeUserPlaylists - two in-flight calls throw "a second
                // operation was started on this context". The id and the roles are already
                // claims on the cookie principal; MusicLibrary.razor.cs reads IsInRole(Admin)
                // off the claims the same way. (Role claims refresh at sign-in, which is what
                // the /account/refresh-signin flow already exists for.)
                userId = GetUserId(authState.User);
                if (userId is null)
                {
                    return;
                }

                _isEmailVerified = !authState.User.IsInRole(Roles.NonValidatedUser);
                await PayPalSubscriptionManagementService.RefreshIfNeededAsync(
                    userId.Value,
                    NavigationManager.BaseUri);
                ShouldShowSubscriptionOffer = !await SubscriptionService.HasActiveSubscriptionAsync(userId.Value);
            }
            else
            {
                ShouldShowSubscriptionOffer = true;
            }

            if (ShouldShowSubscriptionOffer)
            {
                _offerQuote = await PayPalSubscriptionManagementService.GetOfferQuoteAsync(userId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the home page subscription offer island.");
        }
        finally
        {
            _audienceLoaded = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static string BuildReturnUrl(string route, string returnUrl)
        => $"{route}?{ExternalAuthFormFields.ReturnUrl}={Uri.EscapeDataString(returnUrl)}";

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
        if (intervalCount == 1)
        {
            return $"per {intervalUnit}";
        }

        return $"every {intervalCount} {intervalUnit}s";
    }
}
