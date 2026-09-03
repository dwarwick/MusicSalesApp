using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.Routing;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using MusicSalesApp.Helpers;

namespace MusicSalesApp.Components.Shared;

public partial class MobileAppInstallBannerModel : BlazorBase, IAsyncDisposable
{
    private const string AndroidPlatform = "Android";
    private const string IosPlatform = "iOS";
    private const string DismissStorageKey = "streamtunes.mobileAppInstallBanner.dismissed.v1";
    private const string DismissStorageValue = "true";

    private static readonly HashSet<string> SupportedRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/music-library",
        "/login",
        "/register"
    };

    private IJSObjectReference _jsModule;
    private bool _isRouteInScope;
    private bool _shouldEvaluateVisibleBanner;

    protected bool IsBannerVisible { get; private set; }
    protected bool IsPromotional { get; private set; }
    protected string BannerPlatform { get; private set; } = string.Empty;

    private MobileAppInstallOptions Options => MobileAppInstallOptions.Value;

    protected bool ShouldRenderIosSmartAppBanner => Options.Enabled
        && _isRouteInScope
        && !string.IsNullOrWhiteSpace(Options.AppleAppStoreId);

    protected string AppleSmartBannerContent
    {
        get
        {
            var content = $"app-id={Options.AppleAppStoreId}";
            return Uri.TryCreate(NavigationManager.Uri, UriKind.Absolute, out var currentUri)
                ? $"{content}, app-argument={currentUri}"
                : content;
        }
    }

    protected string StreamTunesIconUrl => string.IsNullOrWhiteSpace(Options.StreamTunesIconUrl)
        ? "/images/logo-light-small.png"
        : Options.StreamTunesIconUrl;

    protected string StoreUrl => IsIosBanner
        ? Options.AppleAppStoreUrl
        : Options.GooglePlayUrl;

    protected string StoreBadgeUrl => IsIosBanner
        ? Options.AppleAppStoreBadgeUrl
        : Options.GooglePlayBadgeUrl;

    protected bool HasStoreBadge => !string.IsNullOrWhiteSpace(StoreBadgeUrl);

    protected string StoreBadgeAlt => IsIosBanner
        ? "Download on the App Store"
        : "Get it on Google Play";

    protected string StoreLinkText => StoreBadgeAlt;

    protected string StoreLinkLabel => IsIosBanner
        ? "Download StreamTunes from the App Store"
        : "Get StreamTunes on Google Play";

    protected string BannerSubtitle => IsPromotional
        ? "Open the mobile experience from your app store."
        : "Install the mobile app for a smoother listening experience.";

    private bool IsIosBanner => string.Equals(BannerPlatform, IosPlatform, StringComparison.OrdinalIgnoreCase);

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
        UpdateRouteState();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !_shouldEvaluateVisibleBanner)
        {
            return;
        }

        await EvaluateVisibleBannerAsync();
    }

    protected async Task DismissAsync()
    {
        IsBannerVisible = false;
        await StoreDismissalAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;

        if (_jsModule == null)
        {
            return;
        }

        try
        {
            await _jsModule.DisposeAsync();
        }
        catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
        {
            // Not just a disconnected browser: a circuit being torn down cancels the pending
            // interop call instead, which surfaces as TaskCanceledException.
            Logger.LogDebug(ex, "Mobile app install banner module was already gone at teardown.");
        }
        catch (Exception ex)
        {
            // Nothing may escape DisposeAsync - an exception thrown here is unhandled and
            // destroys the circuit being torn down. Warning, not Error, so a genuine fault stays
            // visible in the log without emailing the admin about a page that is already gone.
            Logger.LogWarning(ex, "Mobile app install banner disposal did not complete cleanly.");
        }
    }

    private void OnLocationChanged(object sender, LocationChangedEventArgs args)
    {
        _ = InvokeAsync(async () =>
        {
            IsBannerVisible = false;
            IsPromotional = false;
            BannerPlatform = string.Empty;
            UpdateRouteState();

            if (_shouldEvaluateVisibleBanner)
            {
                await EvaluateVisibleBannerAsync();
            }

            StateHasChanged();
        });
    }

    private async Task EvaluateVisibleBannerAsync()
    {
        try
        {
            _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Shared/MobileAppInstallBanner.razor.js");
            if (_jsModule == null)
            {
                return;
            }

            var evaluation = await _jsModule.InvokeAsync<MobileAppInstallBannerEvaluation>(
                "evaluateMobileAppInstallBanner",
                CreateClientOptions());

            if (evaluation.ShowBanner && TryApplyEvaluation(evaluation))
            {
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unable to evaluate mobile app install banner visibility.");
        }
    }

    private object CreateClientOptions() => new
    {
        Options.AndroidPackageName,
        Options.GooglePlayUrl,
        Options.AppleAppStoreUrl,
        Options.AndroidFallbackMode,
        Options.ShowIosFallbackBanner,
        DismissStorageKey,
        DismissStorageValue
    };

    private bool TryApplyEvaluation(MobileAppInstallBannerEvaluation evaluation)
    {
        if (!TrySetPlatform(evaluation.Platform))
        {
            return false;
        }

        IsPromotional = evaluation.IsPromotional;
        IsBannerVisible = true;
        return true;
    }

    private bool TrySetPlatform(string platform)
    {
        if (string.Equals(platform, AndroidPlatform, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(Options.GooglePlayUrl))
        {
            BannerPlatform = AndroidPlatform;
            return true;
        }

        if (string.Equals(platform, IosPlatform, StringComparison.OrdinalIgnoreCase)
            && Options.ShowIosFallbackBanner
            && !string.IsNullOrWhiteSpace(Options.AppleAppStoreUrl))
        {
            BannerPlatform = IosPlatform;
            return true;
        }

        return false;
    }

    private async Task StoreDismissalAsync()
    {
        try
        {
            _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Shared/MobileAppInstallBanner.razor.js");
            if (_jsModule != null)
            {
                await _jsModule.InvokeVoidAsync("dismissMobileAppInstallBanner", DismissStorageKey, DismissStorageValue);
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unable to store mobile app install banner dismissal.");
        }
    }

    private bool HasAnyVisibleStoreConfiguration()
    {
        return !string.IsNullOrWhiteSpace(Options.GooglePlayUrl)
            || (Options.ShowIosFallbackBanner && !string.IsNullOrWhiteSpace(Options.AppleAppStoreUrl));
    }

    private void UpdateRouteState()
    {
        _isRouteInScope = IsCurrentRouteInScope();
        _shouldEvaluateVisibleBanner = Options.Enabled
            && _isRouteInScope
            && HasAnyVisibleStoreConfiguration();
    }

    private bool IsCurrentRouteInScope()
    {
        var currentPath = new Uri(NavigationManager.Uri).AbsolutePath.TrimEnd('/');
        if (string.IsNullOrEmpty(currentPath))
        {
            currentPath = "/";
        }

        return SupportedRoutes.Contains(currentPath);
    }
}