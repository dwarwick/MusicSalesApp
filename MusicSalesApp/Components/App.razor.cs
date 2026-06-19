using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;
using System.Text.Encodings.Web;

namespace MusicSalesApp.Components;

public partial class App : ComponentBase
{
    [Inject]
    private IHttpContextAccessor HttpContextAccessor { get; set; } = default!;

    [Inject]
    private IOpenGraphService OpenGraphService { get; set; } = default!;

    private string metaHtml = string.Empty;
    private string googleConsentDefaultHtml = string.Empty;
    private string googleTagManagerHeadHtml = string.Empty;
    private string googleTagManagerBodyHtml = string.Empty;
    private string googleAdsHeadHtml = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        GenerateGoogleTrackingTags();
        await GenerateMetaTags();
    }

    private void GenerateGoogleTrackingTags()
    {
        if (!ShouldRenderGoogleTracking())
        {
            return;
        }

        var googleTagManagerId = Configuration[GoogleAdsTrackingConfigKeys.GoogleTagManagerId];
        var googleAdsTagId = Configuration[GoogleAdsTrackingConfigKeys.TagId];
        if (string.IsNullOrWhiteSpace(googleTagManagerId) && string.IsNullOrWhiteSpace(googleAdsTagId))
        {
            return;
        }

        googleConsentDefaultHtml = BuildGoogleConsentDefaultHtml();

        if (!string.IsNullOrWhiteSpace(googleTagManagerId))
        {
            googleTagManagerHeadHtml = BuildGoogleTagManagerHeadHtml(googleTagManagerId);
            googleTagManagerBodyHtml = BuildGoogleTagManagerBodyHtml(googleTagManagerId);
        }

        if (!string.IsNullOrWhiteSpace(googleAdsTagId))
        {
            googleAdsHeadHtml = BuildGoogleAdsHeadHtml(googleAdsTagId);
        }
    }

    private bool ShouldRenderGoogleTracking()
    {
        if (!Configuration.GetValue<bool>(GoogleAdsTrackingConfigKeys.Enabled))
        {
            return false;
        }

        var requestHost = HttpContextAccessor.HttpContext?.Request.Host.Host;
        if (string.IsNullOrWhiteSpace(requestHost))
        {
            return false;
        }

        var enabledHosts = Configuration
            .GetSection(GoogleAdsTrackingConfigKeys.EnabledHosts)
            .Get<string[]>() ?? Array.Empty<string>();

        return enabledHosts.Contains(requestHost, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildGoogleConsentDefaultHtml()
    {
        return """
            <!-- Google Consent Mode default -->
            <script>
              window.dataLayer = window.dataLayer || [];
              function gtag(){dataLayer.push(arguments);}
              gtag('consent', 'default', {
                'ad_storage': 'denied',
                'analytics_storage': 'denied',
                'ad_user_data': 'denied',
                'ad_personalization': 'denied',
                'wait_for_update': 500
              });
            </script>
            <!-- End Google Consent Mode default -->
            """;
    }

    private static string BuildGoogleTagManagerHeadHtml(string googleTagManagerId)
    {
        var encodedId = JavaScriptEncoder.Default.Encode(googleTagManagerId);

        return $$"""
            <!-- Google Tag Manager -->
            <script>(function(w,d,s,l,i){w[l]=w[l]||[];w[l].push({'gtm.start':
            new Date().getTime(),event:'gtm.js'});var f=d.getElementsByTagName(s)[0],
            j=d.createElement(s),dl=l!='dataLayer'?'&l='+l:'';j.async=true;j.src=
            'https://www.googletagmanager.com/gtm.js?id='+i+dl;f.parentNode.insertBefore(j,f);
            })(window,document,'script','dataLayer','{{encodedId}}');</script>
            <!-- End Google Tag Manager -->
            """;
    }

    private static string BuildGoogleTagManagerBodyHtml(string googleTagManagerId)
    {
        var encodedId = HtmlEncoder.Default.Encode(googleTagManagerId);

        return $$"""
            <!-- Google Tag Manager (noscript) -->
            <noscript><iframe src="https://www.googletagmanager.com/ns.html?id={{encodedId}}"
            height="0" width="0" style="display:none;visibility:hidden"></iframe></noscript>
            <!-- End Google Tag Manager (noscript) -->
            """;
    }

    private static string BuildGoogleAdsHeadHtml(string googleAdsTagId)
    {
        var encodedHtmlId = HtmlEncoder.Default.Encode(googleAdsTagId);
        var encodedJsId = JavaScriptEncoder.Default.Encode(googleAdsTagId);

        return $$"""
            <!-- Google tag (gtag.js) -->
            <script async src="https://www.googletagmanager.com/gtag/js?id={{encodedHtmlId}}"></script>
            <script>
              window.dataLayer = window.dataLayer || [];
              function gtag(){dataLayer.push(arguments);}
              gtag('js', new Date());
              gtag('config', '{{encodedJsId}}');
            </script>
            """;
    }

    private async Task GenerateMetaTags()
    {
        var path = HttpContextAccessor.HttpContext?.Request.Path.Value?.Trim('/') ?? string.Empty;
        
        // Check if this is a song or album page
        if (path.StartsWith("song/") && path.Count(x => x == '/') == 1)
        {
            var songTitle = path.Substring(5); // Remove "song/" prefix
            metaHtml = await OpenGraphService.GenerateSongMetaTagsAsync(songTitle);
        }
        else if (path.StartsWith("album/") && path.Count(x => x == '/') == 1)
        {
            var albumName = path.Substring(6); // Remove "album/" prefix
            metaHtml = await OpenGraphService.GenerateAlbumMetaTagsAsync(albumName);
        }
        else
        {
            metaHtml = string.Empty;
        }
    }
}
