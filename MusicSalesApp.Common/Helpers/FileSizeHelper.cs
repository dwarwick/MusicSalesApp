using System.Text.RegularExpressions;

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Utility methods for formatting file-size values and error messages.
/// </summary>
public static partial class FileSizeHelper
{
    /// <summary>
    /// Rewrites the byte-based error message thrown by Blazor's
    /// <c>IBrowserFile.OpenReadStream()</c> so both the supplied size and the
    /// maximum are shown in MB instead of raw bytes.
    /// If the message doesn't match the expected pattern, it is returned unchanged.
    /// </summary>
    public static string FormatFileSizeExceptionMessage(string message)
    {
        var match = FileSizeExceptionPattern().Match(message);
        if (!match.Success)
            return message;

        var suppliedBytes = long.Parse(match.Groups["supplied"].Value);
        var maxBytes = long.Parse(match.Groups["max"].Value);

        var suppliedMB = suppliedBytes / (1024.0 * 1024.0);
        var maxMB = maxBytes / (1024.0 * 1024.0);

        return $"Supplied file with size {suppliedMB:F2} MB exceeds the maximum of {maxMB:F0} MB.";
    }

    [GeneratedRegex(@"Supplied file with size (?<supplied>\d+)\s*(?:bytes\s+)?exceeds the maximum of (?<max>\d+)\s*(?:bytes)?", RegexOptions.IgnoreCase)]
    private static partial Regex FileSizeExceptionPattern();
}
