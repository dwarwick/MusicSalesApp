#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for TaxBandits OAuth authentication operations.
/// </summary>
public interface ITaxBanditsService
{
    /// <summary>
    /// Gets a Bearer access token from TaxBandits OAuth gateway using a JWS in the Authentication header.
    /// </summary>
    /// <param name="clientId">The TaxBandits client ID.</param>
    /// <param name="userToken">The TaxBandits user token.</param>
    /// <param name="clientSecret">The TaxBandits client secret.</param>
    /// <param name="useSandbox">Whether to use sandbox environment (default: true).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authentication response containing the access token.</returns>
    Task<TaxBanditsAuthResponse> GetAccessTokenAsync(
        string clientId,
        string userToken,
        string clientSecret,
        bool useSandbox = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from TaxBandits authentication endpoint.
/// </summary>
public sealed class TaxBanditsAuthResponse
{
    public int StatusCode { get; set; }
    public string? StatusName { get; set; }
    public string? StatusMessage { get; set; }

    public string? AccessToken { get; set; }
    public string? TokenType { get; set; }
    public int ExpiresIn { get; set; }

    public object? Errors { get; set; }
}
