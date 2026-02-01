#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for Avalara OAuth authentication and W-9/W-8 tax form operations.
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

    /// <summary>
    /// Creates a form request for an embedded W-9 or W-8BEN form via the Avalara Track1099 API.
    /// The response contains the form request data that can be used with the Avalara JavaScript SDK
    /// to display the form in a modal dialog.
    /// </summary>
    /// <param name="formType">The type of form to request: "W-9" or "W-8BEN".</param>
    /// <param name="referenceId">Your internal identifier for the vendor (typically the user's ID).</param>
    /// <param name="ttl">Seconds until this form request should expire (default: 3600, max: 86400).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The form request response containing the form request data for the JavaScript SDK.</returns>
    Task<AvalaraFormRequestResponse> CreateFormRequestAsync(
        string formType,
        string referenceId,
        int ttl = 3600,
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

/// <summary>
/// Response from Avalara form request endpoint.
/// Contains the form request data to be used with the Avalara JavaScript SDK.
/// </summary>
public sealed class AvalaraFormRequestResponse
{
    /// <summary>
    /// Indicates whether the form request was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The complete JSON response from Avalara API to be passed to the JavaScript SDK.
    /// This contains the form_request data structure with id, type, attributes, and links.
    /// </summary>
    public string? FormRequestJson { get; set; }

    /// <summary>
    /// The unique ID of the form request.
    /// </summary>
    public string? FormRequestId { get; set; }

    /// <summary>
    /// The form type (W-9, W-8BEN, or W-8BEN-E).
    /// </summary>
    public string? FormType { get; set; }

    /// <summary>
    /// When the form request expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Error message if the request failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Raw response body for debugging purposes.
    /// </summary>
    public string? RawResponse { get; set; }
}
