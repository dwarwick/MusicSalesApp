namespace MusicSalesApp.Services;

/// <summary>
/// The claims we care about from a Sign in with Apple identity token.
/// </summary>
/// <param name="Subject">
/// Apple's stable per-app user identifier (the <c>sub</c> claim). This is the ONLY durable
/// identity Apple gives us - the email and name are supplied on the first authorization and
/// never again - so it is what we store as the external login provider key.
/// </param>
public sealed record AppleIdentityTokenPayload(
    string Subject,
    string Email,
    bool EmailVerified,
    bool IsPrivateEmail);

public interface IAppleIdentityTokenValidator
{
    /// <summary>
    /// False when no bundle id is configured, mirroring how Google sign-in is feature-flagged
    /// by the presence of its client id/secret.
    /// </summary>
    bool IsConfigured { get; }

    Task<(bool Success, AppleIdentityTokenPayload Payload, string Error)> ValidateAsync(
        string identityToken,
        CancellationToken cancellationToken = default);
}
