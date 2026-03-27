using Microsoft.Extensions.Configuration;

namespace MusicSalesApp.Common.Helpers;

public static class AppConfigHelper
{
    private const string DefaultBaseUrl = "https://streamtunes.net";

    public static string GetBaseUrl(this IConfiguration configuration)
    {
        return configuration["BaseUrl"] ?? DefaultBaseUrl;
    }
}
