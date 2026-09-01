using System.Text.RegularExpressions;
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

/// <summary>
/// Guards the document-level invariants behind the 2026-08-31 production incident, where every
/// script on a /song/{SongTitle} page was requested one directory too deep and returned 404 -
/// blazor.web.js among them, so the circuit never started and nothing was logged but the 404s.
/// </summary>
[TestFixture]
public class AppAssetUrlTests : BUnitTestBase
{
    /// <summary>
    /// Attribute values that are not resolved against the document base URL and so do not need a
    /// leading slash: other origins, protocol-relative URLs, inline data, and in-page anchors.
    /// </summary>
    private static readonly string[] NotDocumentRelative =
        ["http://", "https://", "//", "data:", "mailto:", "tel:", "#"];

    /// <summary>
    /// Syncfusion emits this tag itself, alongside the first component that needs it, and 33.1.44
    /// offers no way to root it or turn it off. App.razor loads the same file from a rooted URL up
    /// front so the duplicate cannot matter - see the comment there. This is the one document-
    /// relative URL left in the page, and it is not ours to fix.
    /// </summary>
    private const string VendorInjectedScript = "_content/Syncfusion.Blazor/scripts/sf-utils.js";

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        TestContext.ComponentFactories.AddStub<ResourcePreloader>();
        ConfigureRequest("streamtunes.net", "/song/Islands in the Stream");
        ConfigureConfiguration();
    }

    [TestCase("Production")]
    [TestCase("Test")]
    [TestCase("Development")]
    public void App_RootsEveryLocalAssetUrl(string environmentName)
    {
        MockWebHostEnvironment.Setup(x => x.EnvironmentName).Returns(environmentName);
        SetupRendererInfo();

        var cut = TestContext.Render<App>();

        var documentRelative = Regex.Matches(cut.Markup, "(?:src|href)=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Where(url => !NotDocumentRelative.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Where(url => !url.StartsWith('/'))
            .Where(url => url != VendorInjectedScript)
            .ToArray();

        Assert.That(
            documentRelative,
            Is.Empty,
            $"These resolve against the document's directory, so on a two-segment route such as "
                + $"/song/{{SongTitle}} they are requested from /song/ and 404: "
                + string.Join(", ", documentRelative));
    }

    [Test]
    public void App_DeclaresCharsetAndBase_BeforeAnythingThatDependsOnThem()
    {
        SetupRendererInfo();

        var cut = TestContext.Render<App>();

        var charset = cut.Markup.IndexOf("<meta charset", StringComparison.OrdinalIgnoreCase);
        var baseTag = cut.Markup.IndexOf("<base ", StringComparison.OrdinalIgnoreCase);
        var firstConsentOrTracking = cut.Markup.IndexOf("dataLayer", StringComparison.Ordinal);
        var manifest = cut.Markup.IndexOf("rel=\"manifest\"", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(charset, Is.GreaterThanOrEqualTo(0));
            Assert.That(baseTag, Is.GreaterThan(charset), "<base> must follow <meta charset>.");

            // <base> only applies to URLs after it. With the manifest link above it, production
            // logged GET /song/manifest.x16vm7vd8l.webmanifest 404.
            Assert.That(manifest, Is.GreaterThan(baseTag), "<base> must precede the manifest link.");

            // The consent and GTM blocks are configuration-driven and grow. Leaving charset below
            // them put it at byte 998 of the response, 26 bytes short of the 1024-byte limit past
            // which browsers ignore it.
            Assert.That(
                firstConsentOrTracking,
                Is.GreaterThan(charset),
                "<meta charset> must precede the consent/GTM blocks so it stays inside 1024 bytes.");
        });
    }

    [Test]
    public void App_DoesNotEmitTheEnvironmentTagHelper_WhichDoesNotRunInRazorComponents()
    {
        MockWebHostEnvironment.Setup(x => x.EnvironmentName).Returns("Production");
        SetupRendererInfo();

        var cut = TestContext.Render<App>();

        // Emitted literally it loaded the stylesheet in every environment, and as an unknown
        // element in <head> it ended the head, taking ImportMap and HeadOutlet into the body.
        Assert.That(cut.Markup, Does.Not.Contain("<environment"));
    }

    [TestCase("Production", true)]
    [TestCase("Test", false)]
    [TestCase("Development", false)]
    public void App_LinksTheSeoFrameworkUiStylesheet_InProductionOnly(string environmentName, bool expected)
    {
        MockWebHostEnvironment.Setup(x => x.EnvironmentName).Returns(environmentName);
        SetupRendererInfo();

        var cut = TestContext.Render<App>();

        Assert.That(cut.Markup.Contains("seo-hide-framework-ui"), Is.EqualTo(expected), environmentName);
    }

    private void ConfigureRequest(string host, string path)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString(host);
        httpContext.Request.Path = path;

        MockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);
    }

    private void ConfigureConfiguration()
    {
        // Google tracking is switched on for streamtunes.net so the consent and GTM blocks render.
        // The charset ordering assertion is about staying ahead of those blocks, so a run without
        // them would pass while proving nothing.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Facebook:AppId"] = "test-facebook-app-id",
                [GoogleAdsTrackingConfigKeys.Enabled] = "true",
                [GoogleAdsTrackingConfigKeys.TagId] = "AW-18188763957",
                [GoogleAdsTrackingConfigKeys.GoogleTagManagerId] = "GTM-KHSQBX5D",
                [$"{GoogleAdsTrackingConfigKeys.EnabledHosts}:0"] = "streamtunes.net"
            })
            .Build();

        TestContext.Services.AddSingleton<IConfiguration>(configuration);
    }
}
