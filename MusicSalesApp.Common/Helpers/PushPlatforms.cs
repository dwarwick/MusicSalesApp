namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for <c>PushDeviceToken.Platform</c>.
/// </summary>
/// <remarks>
/// The value decides which transport a token is sent through, and the two are not interchangeable:
/// Android tokens go to Firebase Cloud Messaging, iOS tokens go straight to APNs. A mismatch is not
/// a soft failure - the wrong service rejects the token as malformed - so this is written by the
/// registering client and read by the dispatcher, which is exactly the pairing that needs a shared
/// constant rather than a literal.
/// </remarks>
public static class PushPlatforms
{
    public const string Android = "Android";

    public const string Ios = "iOS";

    /// <summary>Platforms a client may register. Used for input validation.</summary>
    public static readonly string[] All = [Android, Ios];

    /// <summary>
    /// Normalises a client-supplied platform to one of <see cref="All"/>, or null when it is not
    /// one we can deliver to. Case-insensitive because the value crosses a JSON boundary.
    /// </summary>
    public static string Normalize(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return null;
        }

        foreach (var candidate in All)
        {
            if (string.Equals(candidate, platform.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
