#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for Avalara OAuth authentication.
/// </summary>
public interface IAvalaraTaxService
{
    /// <summary>
    /// Gets a Bearer access token from Avalara OAuth endpoint using client credentials flow.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authentication response containing the access token.</returns>
    Task<AvalaraAuthResponse> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a Bearer access token from Avalara OAuth endpoint using explicit credentials.
    /// </summary>
    /// <param name="clientId">The Avalara client ID.</param>
    /// <param name="clientSecret">The Avalara client secret.</param>
    /// <param name="useSandbox">Whether to use sandbox environment (default: true).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authentication response containing the access token.</returns>
    Task<AvalaraAuthResponse> GetAccessTokenAsync(
        string clientId,
        string clientSecret,
        bool useSandbox = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from Avalara authentication endpoint.
/// </summary>
public sealed class AvalaraAuthResponse
{
    /// <summary>
    /// Indicates whether the authentication request was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The access token to use for API requests.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// The type of token (typically "Bearer").
    /// </summary>
    public string? TokenType { get; set; }

    /// <summary>
    /// The number of seconds until the token expires.
    /// </summary>
    public int ExpiresIn { get; set; }

    /// <summary>
    /// Error message if the authentication failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Raw response body for debugging purposes.
    /// </summary>
    public string? RawResponse { get; set; }
}
