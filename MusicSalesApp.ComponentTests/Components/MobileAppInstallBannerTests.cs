using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class MobileAppInstallBannerTests : BUnitTestBase
{
    private const string GooglePlayUrl = "https://play.google.com/store/apps/details?id=net.streamtunes.musicsalesapp.maui";
    private const string GooglePlayBadgeUrl = "https://play.google.com/intl/en_us/badges/static/images/badges/en_badge_web_generic.png";
    private Mock<IJSRuntime> _mockJsRuntime = default!;
    private Mock<IJSObjectReference> _mockJsModule = default!;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        _mockJsRuntime = new Mock<IJSRuntime>();
        _mockJsModule = new Mock<IJSObjectReference>();
    }

    [Test]
    public void Banner_DoesNotRender_WhenDisabled()
    {
        ConfigureOptions(new MobileAppInstallOptions
        {
            Enabled = false,
            GooglePlayUrl = GooglePlayUrl,
            GooglePlayBadgeUrl = GooglePlayBadgeUrl
        });
        NavigateTo("/");

        var cut = TestContext.Render<MobileAppInstallBanner>();

        Assert.That(cut.Markup, Does.Not.Contain("mobile-app-install-banner"));
    }

    [Test]
    public void Banner_DoesNotEvaluate_WhenRouteIsOutOfScope()
    {
        ConfigureOptions(CreateEnabledAndroidOptions());
        ConfigureJsEvaluation(new MobileAppInstallBannerEvaluation
        {
            ShowBanner = true,
            Platform = "Android"
        });
        NavigateTo("/manage-account");

        var cut = TestContext.Render<MobileAppInstallBanner>();

        Assert.That(cut.Markup, Does.Not.Contain("mobile-app-install-banner"));
        _mockJsRuntime.Verify(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()), Times.Never);
    }

    [Test]
    public void Banner_RendersAndroidInstallPrompt_WhenDetectionShowsNotInstalled()
    {
        ConfigureOptions(CreateEnabledAndroidOptions());
        ConfigureJsEvaluation(new MobileAppInstallBannerEvaluation
        {
            ShowBanner = true,
            Platform = "Android",
            IsPromotional = false
        });
        NavigateTo("/music-library");

        var cut = TestContext.Render<MobileAppInstallBanner>();

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.Markup, Does.Contain("mobile-app-install-banner"));
                Assert.That(cut.Markup, Does.Contain("data-mobile-app-install-platform=\"Android\""));
                Assert.That(cut.Markup, Does.Contain("data-mobile-app-install-promotional=\"false\""));
                Assert.That(cut.Markup, Does.Contain(GooglePlayUrl));
                Assert.That(cut.Markup, Does.Contain(GooglePlayBadgeUrl));
                Assert.That(cut.Markup, Does.Contain("/images/logo-light-small.png"));
                Assert.That(cut.Markup, Does.Contain("Get it on Google Play"));
            });
        });
    }

    [Test]
    public void Banner_RendersAndroidPromotionalPrompt_WhenFallbackModeAllowsPromotion()
    {
        var options = CreateEnabledAndroidOptions();
        options.AndroidFallbackMode = MobileAppInstallFallbackModes.ShowPromotionalBanner;
        ConfigureOptions(options);
        ConfigureJsEvaluation(new MobileAppInstallBannerEvaluation
        {
            ShowBanner = true,
            Platform = "Android",
            IsPromotional = true
        });
        NavigateTo("/login");

        var cut = TestContext.Render<MobileAppInstallBanner>();

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.Markup, Does.Contain("data-mobile-app-install-promotional=\"true\""));
                Assert.That(cut.Markup, Does.Contain("Open the mobile experience from your app store."));
            });
        });
    }

    [Test]
    public void Banner_DoesNotRenderIosFallback_WhenAppStoreIsNotProductionConfigured()
    {
        ConfigureOptions(new MobileAppInstallOptions
        {
            Enabled = true,
            AppleAppStoreId = string.Empty,
            AppleAppStoreUrl = string.Empty,
            ShowIosFallbackBanner = false
        });
        ConfigureJsEvaluation(new MobileAppInstallBannerEvaluation
        {
            ShowBanner = false,
            Platform = "iOS"
        });
        NavigateTo("/register");

        var cut = TestContext.Render<MobileAppInstallBanner>();

        Assert.That(cut.Markup, Does.Not.Contain("mobile-app-install-banner"));
        Assert.That(cut.Markup, Does.Not.Contain("apple-itunes-app"));
    }

    private void ConfigureOptions(MobileAppInstallOptions options)
    {
        TestContext.Services.AddSingleton<IOptions<MobileAppInstallOptions>>(Options.Create(options));
    }

    private void ConfigureJsEvaluation(MobileAppInstallBannerEvaluation evaluation)
    {
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .Returns(new ValueTask<IJSObjectReference>(_mockJsModule.Object));

        _mockJsModule
            .Setup(x => x.InvokeAsync<MobileAppInstallBannerEvaluation>("evaluateMobileAppInstallBanner", It.IsAny<object[]>()))
            .Returns(new ValueTask<MobileAppInstallBannerEvaluation>(evaluation));

        _mockJsModule
            .Setup(x => x.InvokeAsync<object>("dismissMobileAppInstallBanner", It.IsAny<object[]>()))
            .Returns(new ValueTask<object>(new object()));

        TestContext.Services.AddSingleton<IJSRuntime>(_mockJsRuntime.Object);
    }

    private void NavigateTo(string uri)
    {
        TestContext.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
    }

    private static MobileAppInstallOptions CreateEnabledAndroidOptions() => new()
    {
        Enabled = true,
        AndroidPackageName = "net.streamtunes.musicsalesapp.maui",
        GooglePlayUrl = GooglePlayUrl,
        GooglePlayBadgeUrl = GooglePlayBadgeUrl,
        StreamTunesIconUrl = "/images/logo-light-small.png",
        AndroidFallbackMode = MobileAppInstallFallbackModes.Hide
    };
}