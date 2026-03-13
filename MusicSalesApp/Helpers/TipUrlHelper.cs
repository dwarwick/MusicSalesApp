#nullable enable

namespace MusicSalesApp.Helpers;

/// <summary>
/// Helpers for manipulating tip-related PayPal return URLs.
/// </summary>
public static class TipUrlHelper
{
    private static readonly HashSet<string> TipQueryKeys =
        new(StringComparer.OrdinalIgnoreCase) { "tip_status", "token", "PayerID" };

    /// <summary>
    /// Extracts the last tip_status and its associated token from a URL that may
    /// contain accumulated query parameters from multiple PayPal round-trips.
    /// </summary>
    public static (string? Status, string? Token) GetLastTipParams(string url)
    {
        var qIndex = url.IndexOf('?');
        if (qIndex < 0) return (null, null);

        var pairs = url[(qIndex + 1)..].Split('&');
        string? lastStatus = null;
        string? lastToken = null;

        for (int i = 0; i < pairs.Length; i++)
        {
            var eqIdx = pairs[i].IndexOf('=');
            if (eqIdx < 0) continue;
            var key = pairs[i][..eqIdx];
            var value = Uri.UnescapeDataString(pairs[i][(eqIdx + 1)..]);

            if (key.Equals("tip_status", StringComparison.OrdinalIgnoreCase))
            {
                lastStatus = value;
                lastToken = null; // reset so we pick up the token that follows this status
            }
            else if (key.Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                lastToken = value;
            }
        }

        return (lastStatus, lastToken);
    }

    /// <summary>
    /// Strips tip-related query parameters (tip_status, token, PayerID) from a URL
    /// to prevent accumulation across multiple PayPal round-trips.
    /// </summary>
    public static string StripTipQueryParams(string url)
    {
        var qIndex = url.IndexOf('?');
        if (qIndex < 0) return url;

        var basePath = url[..qIndex];
        var query = url[(qIndex + 1)..];

        var kept = new List<string>();
        foreach (var param in query.Split('&'))
        {
            var eqIndex = param.IndexOf('=');
            var key = eqIndex >= 0 ? param[..eqIndex] : param;
            if (!TipQueryKeys.Contains(key))
            {
                kept.Add(param);
            }
        }

        return kept.Count > 0 ? $"{basePath}?{string.Join('&', kept)}" : basePath;
    }
}
