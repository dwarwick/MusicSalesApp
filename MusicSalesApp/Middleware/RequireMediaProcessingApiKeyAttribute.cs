using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Middleware;

/// <summary>
/// Authorises the Azure Function's callbacks into the site.
///
/// <para>
/// Deliberately a separate secret and a separate header from <see cref="RequireMobileApiKeyAttribute"/>.
/// The mobile key ships inside a distributed app binary and must be assumed to be extractable; this
/// key authorises writes to the song catalogue and lives only in server-side configuration and the
/// Function App's settings. Sharing one key between the two would give anyone who unpacked the
/// mobile app the ability to publish songs.
/// </para>
///
/// <para>
/// This is the only reader of the secret on this side, and configuration is its only source. Do not
/// mirror it onto <c>MediaProcessingOptions</c> "for consistency" - a second copy that silently
/// disagrees would show up as the Function's callbacks failing authorisation while both values
/// looked right wherever you happened to check.
/// </para>
///
/// <para>Fails closed when unconfigured, matching the mobile filter.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireMediaProcessingApiKeyAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedKey = configuration[AppSettingKeys.MediaProcessingApiKey];

        if (string.IsNullOrEmpty(expectedKey))
        {
            context.Result = new UnauthorizedObjectResult(new { message = "API key not configured." });
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(
                MediaProcessingRoutes.ApiKeyHeaderName,
                out var providedKey)
            || !string.Equals(expectedKey, providedKey, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Invalid or missing API key." });
            return;
        }

        await next();
    }
}
