namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// The two authentication schemes that handle Google sign-in, and the callback path each one owns.
///
/// <para>
/// Web and mobile used to share a single scheme and a single callback path. That worked, but it
/// left no way to tell the two flows apart when a sign-in failed: the OAuth state carries the
/// distinction, and a state failure is precisely the case where the state cannot be read. So a
/// failed web sign-in and a failed mobile sign-in were indistinguishable, and neither could be
/// returned to the right place.
/// </para>
/// </summary>
public static class GoogleAuthSchemes
{
    /// <summary>
    /// The scheme handling browser sign-in, on its own callback path.
    /// </summary>
    /// <remarks>
    /// This is the scheme name only. The <em>provider</em> name recorded against the user stays
    /// <see cref="ExternalLoginProviders.Google"/> - see <see cref="WebCallbackPath"/>.
    /// </remarks>
    public const string Web = "GoogleWeb";

    /// <summary>
    /// Web callback path. New, so it must be registered in the Google Cloud Console as an
    /// authorized redirect URI before a deploy, or every browser sign-in fails with
    /// <c>redirect_uri_mismatch</c>.
    /// </summary>
    public const string WebCallbackPath = "/signin-google";

    /// <summary>
    /// The scheme handling the MAUI app's sign-in. Deliberately still named
    /// <see cref="ExternalLoginProviders.Google"/> and still on the original callback path, so
    /// released app builds and the redirect URI already registered with Google keep working.
    /// </summary>
    public const string Mobile = ExternalLoginProviders.Google;

    /// <summary>Mobile callback path, unchanged since before the split.</summary>
    public const string MobileCallbackPath = "/signin-google-mobile";
}
