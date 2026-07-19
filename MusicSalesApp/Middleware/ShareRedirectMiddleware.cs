using System.Text.RegularExpressions;
using System.Web;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Middleware;

/// <summary>
/// Middleware that handles /share/{id} requests by serving a minimal HTML page
/// with OG meta tags (for Facebook's crawler) and a meta redirect to the
/// canonical /song/{title} page (for real users).
/// Runs before Blazor's routing pipeline to avoid component lifecycle issues.
/// </summary>
public partial class ShareRedirectMiddleware
{
    private readonly RequestDelegate _next;

    public ShareRedirectMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context,
        IOpenGraphService openGraphService,
        ISongMetadataService songMetadataService,
        ILogger<ShareRedirectMiddleware> logger)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        var match = SharePathRegex().Match(path);

        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var songId))
        {
            await _next(context);
            return;
        }

        try
        {
            logger.LogInformation("[ShareRedirect] Handling /share/{SongId}", songId);

            var song = await songMetadataService.GetByIdAsync(songId);
            if (song == null)
            {
                logger.LogWarning("[ShareRedirect] Song {SongId} not found", songId);
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("Song not found");
                return;
            }

            var ogTags = await openGraphService.GenerateSongMetaTagsByIdAsync(songId);
            var songTitle = SongTitleHelper.GetEffectiveTitle(
                song.SongTitle, song.Mp3BlobPath, song.BlobPath);
            var encodedTitle = Uri.EscapeDataString(songTitle);
            var redirectUrl = $"/song/{encodedTitle}";

            logger.LogInformation("[ShareRedirect] Redirecting to {RedirectUrl}", redirectUrl);

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.StatusCode = 200;

            var appLink = $"streamtunes://share/{songId}";

            await context.Response.WriteAsync($$"""
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset="utf-8" />
                    <meta name="viewport" content="width=device-width, initial-scale=1" />
                    <title>{{HttpUtility.HtmlEncode(songTitle)}} - StreamTunes</title>
                    {{ogTags}}
                    <style>
                        body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #121212; color: #fff; }
                        .banner { display: flex; align-items: center; justify-content: space-between; padding: 12px 16px; background: #1db954; }
                        .banner-text { font-size: 14px; font-weight: 600; }
                        .banner-btn { background: #fff; color: #1db954; border: none; border-radius: 20px; padding: 8px 20px; font-size: 14px; font-weight: 700; cursor: pointer; text-decoration: none; white-space: nowrap; }
                    </style>
                    <script>
                        // Try to open the app immediately via custom scheme.
                        // If the app is installed, Android will switch to it.
                        // If not, nothing visible happens and the page stays.
                        window.location.href = "{{appLink}}";
                        // Fallback: redirect to web page after 1.5 seconds
                        // (only fires if the app didn't open)
                        setTimeout(function() {
                            window.location.replace("{{HttpUtility.JavaScriptStringEncode(redirectUrl)}}");
                        }, 1500);
                    </script>
                </head>
                <body>
                    <div class="banner">
                        <span class="banner-text">Listen in the StreamTunes app</span>
                        <a class="banner-btn" href="{{HttpUtility.HtmlAttributeEncode(appLink)}}">Open App</a>
                    </div>
                    <p style="padding: 20px; text-align: center; color: #b3b3b3;">
                        Redirecting to <a href="{{HttpUtility.HtmlAttributeEncode(redirectUrl)}}" style="color: #1db954;">{{HttpUtility.HtmlEncode(songTitle)}}</a>...
                    </p>
                </body>
                </html>
                """);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ShareRedirect] Error handling /share/{SongId}", songId);

            // Fallback: simple 302 redirect to homepage rather than letting the
            // exception handler show the Blazor error page
            if (!context.Response.HasStarted)
            {
                context.Response.Redirect("/");
            }
        }
    }

    [GeneratedRegex(@"^/share/(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SharePathRegex();
}
