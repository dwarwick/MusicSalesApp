using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Middleware;

public sealed class DevelopmentPayPalReturnRedirectMiddleware(
    RequestDelegate next,
    IHostEnvironment environment,
    IConfiguration configuration)
{
    private static readonly string[] NgrokHostSuffixes = [".ngrok-free.dev", ".ngrok.io"];

    public Task InvokeAsync(HttpContext context)
    {
        if (TryBuildLocalReturnUrl(context.Request, out var localReturnUrl))
        {
            context.Response.Redirect(localReturnUrl);
            return Task.CompletedTask;
        }

        return next(context);
    }

    private bool TryBuildLocalReturnUrl(HttpRequest request, out string localReturnUrl)
    {
        localReturnUrl = string.Empty;

        if (!environment.IsDevelopment()
            || !IsNgrokHost(request.Host.Host)
            || !string.Equals(request.Path.Value, AppPageRoutes.ManageAccount, StringComparison.OrdinalIgnoreCase)
            || !request.Query.ContainsKey(PayPalReturnQueryKeys.Success))
        {
            return false;
        }

        var configuredBaseUrl = configuration[AppSettingKeys.DevelopmentLocalBaseUrl];
        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        localReturnUrl = $"{configuredBaseUrl!.TrimEnd('/')}{request.PathBase}{request.Path}{request.QueryString}";
        return true;
    }

    private static bool IsNgrokHost(string host)
        => NgrokHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
