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

    /// <summary>
    /// Reports multiple 1099-NEC transactions to TaxBandits for US-based creators in a single batch.
    /// This should be called after payouts are successfully sent to US creators.
    /// TaxBandits will track these transactions for end-of-year 1099-NEC filing.
    /// </summary>
    /// <param name="transactions">List of transactions to report.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from TaxBandits API.</returns>
    Task<Form1099TransactionResponse> ReportForm1099TransactionsBatchAsync(
        List<Form1099Transaction> transactions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of all W-9/W-8 certificate requests for a specific recipient (payee).
    /// Used to check if a returning creator already has a valid tax form on file.
    /// </summary>
    /// <param name="payeeRef">The PayeeRef (email) used in the original W-9/W-8 request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status response from TaxBandits API.</returns>
    Task<WhCertificateStatusResponse> GetWhCertificateStatusAsync(
        string payeeRef,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a transient token from TaxBandits for the Drop-in UI embedded form.
    /// The token is valid for 15 minutes and bound to the specified origins.
    /// </summary>
    /// <param name="origins">The allowed origins (domains) for the Drop-in UI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transient token response.</returns>
    Task<TransientTokenResponse> GetTransientTokenAsync(
        List<string> origins,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Initiates an Instant TIN Matching request with TaxBandits.
    /// Called after a W-9 form is completed to verify the TIN/name combination with the IRS.
    /// See: https://developer.taxbandits.com/docs/InstantTINMatching/Request
    /// </summary>
    /// <param name="request">The TIN matching request details extracted from the W-9 webhook payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from TaxBandits Instant TIN Matching API.</returns>
    Task<InstantTinMatchResponse> RequestInstantTinMatchAsync(
        InstantTinMatchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a single 1099-NEC transaction to report to TaxBandits.
/// </summary>
public sealed class Form1099Transaction
{
    /// <summary>
    /// The PayeeRef (email) of the creator.
    /// </summary>
    public required string PayeeRef { get; set; }

    /// <summary>
    /// A unique identifier for this transaction (e.g., PayPal transaction ID).
    /// </summary>
    public required string SequenceId { get; set; }

    /// <summary>
    /// The date of the transaction.
    /// </summary>
    public DateTime TransactionDate { get; set; }

    /// <summary>
    /// The gross amount of the payout (before any withholding).
    /// </summary>
    public decimal GrossAmount { get; set; }

    /// <summary>
    /// The amount withheld for backup withholding (if any).
    /// </summary>
    public decimal WithheldAmount { get; set; }
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

/// <summary>
/// Response from TaxBandits Form1099Transactions API.
/// </summary>
public sealed class Form1099TransactionResponse
{
    public bool Success { get; set; }
    
    /// <summary>
    /// The transaction ID (SubmissionId) returned by TaxBandits for tracking.
    /// This can be used to update StreamPayout records.
    /// </summary>
    public string? TransactionId { get; set; }
    
    /// <summary>
    /// The status message from TaxBandits (e.g., "Transactions saved successfully").
    /// </summary>
    public string? StatusMessage { get; set; }
    
    public string? ErrorMessage { get; set; }
    public string? RawResponse { get; set; }
}

/// <summary>
/// Response from TaxBandits WhCertificate/Status endpoint.
/// </summary>
public sealed class WhCertificateStatusResponse
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RawResponse { get; set; }
    
    /// <summary>
    /// Total number of certificate records for this payee.
    /// </summary>
    public int TotalRecords { get; set; }
    
    /// <summary>
    /// List of certificate statuses (most recent first).
    /// </summary>
    public List<WhCertificateRecord> Records { get; set; } = new();
    
    /// <summary>
    /// Whether the payee has at least one valid (COMPLETED) W-9 or W-8 certificate.
    /// </summary>
    public bool HasValidCertificate => Records.Any(r =>
        r.FormStatus == "COMPLETED" ||
        r.FormStatus == "COMPLETED_AND_TIN_MATCH_INPROGRESS" ||
        r.FormStatus == "ORDER_NOT_CREATED" /* TIN type not applicable for matching */);
}

/// <summary>
/// Represents a single W-9/W-8 certificate record from TaxBandits.
/// </summary>
public sealed class WhCertificateRecord
{
    public string? SubmissionId { get; set; }
    public string? FormType { get; set; }
    public string? FormStatus { get; set; }
    public string? StatusTimestamp { get; set; }
    public string? TinMatchingStatus { get; set; }
    public string? TinMatchingStatusTimestamp { get; set; }
}

/// <summary>
/// Response from TaxBandits transient token endpoint for Drop-in UI.
/// </summary>
public sealed class TransientTokenResponse
{
    public bool Success { get; set; }
    public string? TransientToken { get; set; }
    public string? TokenType { get; set; }
    public int ExpiresIn { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Request model for Instant TIN Matching API.
/// See: https://developer.taxbandits.com/docs/InstantTINMatching/Request
/// </summary>
public sealed class InstantTinMatchRequest
{
    /// <summary>
    /// The TIN type: SSN, EIN, or ITIN.
    /// </summary>
    public required string TINType { get; set; }

    /// <summary>
    /// The 9-digit TIN (with or without hyphens).
    /// </summary>
    public required string TIN { get; set; }

    /// <summary>
    /// First name (required for SSN/ITIN).
    /// </summary>
    public string? FirstNm { get; set; }

    /// <summary>
    /// Last name (required for SSN/ITIN).
    /// </summary>
    public string? LastNm { get; set; }

    /// <summary>
    /// Middle name (optional).
    /// </summary>
    public string? MiddleNm { get; set; }

    /// <summary>
    /// Business name (required for EIN).
    /// </summary>
    public string? BusinessNm { get; set; }

    /// <summary>
    /// The user ID in our system, used for tracking.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// The user's email, used for tracking.
    /// </summary>
    public string? Email { get; set; }
}

/// <summary>
/// Response from TaxBandits Instant TIN Matching API.
/// See: https://developer.taxbandits.com/docs/InstantTINMatching/Request
/// </summary>
public sealed class InstantTinMatchResponse
{
    public bool Success { get; set; }

    /// <summary>
    /// The unique record ID for this TIN match request.
    /// </summary>
    public string? RecordId { get; set; }

    /// <summary>
    /// The TIN status code: TIN-001 (SUCCESS), TIN-002 (FAILED), TIN-003 (ON HOLD).
    /// </summary>
    public string? TINStatusCode { get; set; }

    /// <summary>
    /// The TIN status text: SUCCESS, FAILED, ON HOLD.
    /// </summary>
    public string? TINStatus { get; set; }

    /// <summary>
    /// Human-readable TIN status message.
    /// </summary>
    public string? TINStatusMsg { get; set; }

    public string? ErrorMessage { get; set; }
    public string? RawResponse { get; set; }
}
