namespace MusicSalesApp.Services;

/// <summary>
/// The Sign in with Apple REST API, used for the two calls that need a client secret:
/// exchanging the authorization code for a refresh token at sign-in, and revoking that token
/// when the account is deleted.
/// </summary>
/// <remarks>
/// Apple requires an app that offers Sign in with Apple to revoke the user's tokens on account
/// deletion. That is the only reason this exists - ordinary sign-in needs nothing from here,
/// because the identity token is verified against Apple's public keys by
/// <see cref="IAppleIdentityTokenValidator"/>.
/// </remarks>
public interface IAppleTokenRevocationService
{
    /// <summary>
    /// False until a Sign in with Apple key is configured, mirroring how Google sign-in is
    /// feature-flagged by the presence of its client id/secret. Sign-in still works when false;
    /// only revocation-on-delete is unavailable.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Trades the one-shot authorization code from the native sheet for a refresh token, which is
    /// the credential the revoke endpoint needs later. Returns null if unconfigured or refused.
    /// </summary>
    Task<string> ExchangeAuthorizationCodeAsync(string authorizationCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the token and, with it, the user's Sign in with Apple grant for this app.
    /// </summary>
    Task<bool> RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
}
