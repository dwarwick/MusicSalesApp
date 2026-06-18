using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class AppGoogleTrackingTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        TestContext.ComponentFactories.AddStub<ResourcePreloader>();
    }

    [TestCase("Production", "streamtunes.net", "www.streamtunes.net")]
    [TestCase("Test", "davidtest.dev", "www.davidtest.dev")]
    [TestCase("Development", "localhost", "127.0.0.1")]
    public void App_RendersGtmAndGoogleAdsTags_ForAllowedEnvironmentHosts(
        string environmentName,
        string requestHost,
        string secondaryHost)
    {
        ConfigureRequestHost(requestHost);
        ConfigureGoogleAdsTracking(enabled: true, requestHost, secondaryHost);
        SetupRendererInfo();

        var cut = TestContext.Render<App>();

        Assert.That(cut.Markup, Does.Contain("GTM-KHSQBX5D"));
        Assert.That(cut.Markup, Does.Contain("googletagmanager.com/gtm.js"));
        Assert.That(cut.Markup, Does.Contain("googletagmanager.com/ns.html?id=GTM-KHSQBX5D"));
        Assert.That(cut.Markup, Does.Contain("googletagmanager.com/gtag/js?id=AW-18188763957"));
        Assert.That(cut.Markup, Does.Contain("gtag('config', 'AW-18188763957')"), environmentName);
    }

    [Test]
    public void App_RendersGtmNoscriptImmediatelyAfterOpeningBody_ForAllowedHost()
    {
        ConfigureRequestHost("davidtest.dev");
        ConfigureGoogleAdsTracking(enabled: true, "davidtest.dev");
        SetupRendererInfo();

        var cut = TestContext.Render<App>();

        var bodyIndex = cut.Markup.IndexOf("<body>", StringComparison.OrdinalIgnoreCase);
        var noscriptIndex = cut.Markup.IndexOf("<!-- Google Tag Manager (noscript) -->", StringComparison.Ordinal);

        Assert.That(bodyIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(noscriptIndex, Is.GreaterThan(bodyIndex));

        var betweenBodyAndNoscript = cut.Markup[(bodyIndex + "<body>".Length)..noscriptIndex];
        Assert.That(string.IsNullOrWhiteSpace(betweenBodyAndNoscript), Is.True);
    }

    [Test]
    public void App_DoesNotRenderGoogleTracking_WhenHostIsNotAllowed()
    {
        ConfigureRequestHost("example.com");
        ConfigureGoogleAdsTracking(enabled: true, "davidtest.dev");
        SetupRendererInfo();

        var cut = TestContext.Render<App>();

        Assert.That(cut.Markup, Does.Not.Contain("GTM-KHSQBX5D"));
        Assert.That(cut.Markup, Does.Not.Contain("googletagmanager.com/gtag/js?id=AW-18188763957"));
    }

    [Test]
    public void App_DoesNotRenderGoogleTracking_WhenDisabled()
    {
        ConfigureRequestHost("davidtest.dev");
        ConfigureGoogleAdsTracking(enabled: false, "davidtest.dev");
        SetupRendererInfo();

        var cut = TestContext.Render<App>();

        Assert.That(cut.Markup, Does.Not.Contain("GTM-KHSQBX5D"));
        Assert.That(cut.Markup, Does.Not.Contain("googletagmanager.com/gtag/js?id=AW-18188763957"));
    }

    private void ConfigureRequestHost(string host)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString(host);
        httpContext.Request.Path = "/";

        MockHttpContextAccessor
            .Setup(x => x.HttpContext)
            .Returns(httpContext);
    }

    private void ConfigureGoogleAdsTracking(bool enabled, params string[] enabledHosts)
    {
        var configValues = new Dictionary<string, string>
            {
                ["Facebook:AppId"] = "test-facebook-app-id",
                [GoogleAdsTrackingConfigKeys.Enabled] = enabled.ToString(),
                [GoogleAdsTrackingConfigKeys.TagId] = "AW-18188763957",
                [GoogleAdsTrackingConfigKeys.CreatorSignupConversionLabel] = "zvw_CJ6in74cELWGiuFD",
                [GoogleAdsTrackingConfigKeys.GoogleTagManagerId] = "GTM-KHSQBX5D"
            };

        for (var i = 0; i < enabledHosts.Length; i++)
        {
            configValues[$"{GoogleAdsTrackingConfigKeys.EnabledHosts}:{i}"] = enabledHosts[i];
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        TestContext.Services.AddSingleton<IConfiguration>(configuration);
    }
}
