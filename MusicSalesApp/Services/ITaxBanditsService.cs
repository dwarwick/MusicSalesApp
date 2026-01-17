#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for TaxBandits OAuth authentication and W-9/W-8 tax form operations.
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

    /// <summary>
    /// Requests a W-9/W-8 tax form via email using the TaxBandits API.
    /// TaxBandits will send an email to the recipient with a link to complete their tax form.
    /// </summary>
    /// <param name="userId">The user ID in our system.</param>
    /// <param name="email">The email address of the recipient.</param>
    /// <param name="baseUrl">The base URL of the application for email templates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from TaxBandits API.</returns>
    Task<W9RequestResponse> RequestW9ByEmailAsync(
        int userId,
        string email,
        string baseUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an incomplete W-9/W-8 form from TaxBandits.
    /// This is used when a user abandons the form and needs to request a new one.
    /// </summary>
    /// <param name="payeeRef">The PayeeRef (email) used in the original W-9 request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response indicating success or failure.</returns>
    Task<W9DeleteResponse> DeleteW9Async(
        string payeeRef,
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

/// <summary>
/// Response from TaxBandits W-9 request API.
/// </summary>
public sealed class W9RequestResponse
{
    public bool Success { get; set; }
    public string? SubmissionId { get; set; }
    public string? Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RawResponse { get; set; }
}

/// <summary>
/// Response from TaxBandits W-9 delete API.
/// </summary>
public sealed class W9DeleteResponse
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RawResponse { get; set; }
}
