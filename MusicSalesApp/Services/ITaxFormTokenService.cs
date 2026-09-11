#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// How a tax-form token request ended, so one implementation can serve both an HTTP endpoint and
/// an in-process caller without either having to re-derive the status code.
/// </summary>
public enum TaxFormTokenOutcome
{
    /// <summary>A transient token was issued.</summary>
    Success,

    /// <summary>
    /// The caller asked for something that cannot be granted - no creator record, no pending tax
    /// form, no payee reference. The controller reports these as 400.
    /// </summary>
    InvalidRequest,

    /// <summary>
    /// Configuration is missing or TaxBandits refused. The controller reports these as 500.
    /// </summary>
    ServerError
}

/// <summary>
/// The result of a tax-form token request.
/// </summary>
/// <param name="Outcome">Which of the three cases occurred.</param>
/// <param name="Response">Populated only when <paramref name="Outcome"/> is
/// <see cref="TaxFormTokenOutcome.Success"/>.</param>
/// <param name="ErrorMessage">User-facing explanation for the two failure outcomes.</param>
public sealed record TaxFormTokenResult(
    TaxFormTokenOutcome Outcome,
    TaxFormTokenResponse? Response,
    string? ErrorMessage)
{
    public static TaxFormTokenResult Success(TaxFormTokenResponse response) =>
        new(TaxFormTokenOutcome.Success, response, null);

    public static TaxFormTokenResult InvalidRequest(string errorMessage) =>
        new(TaxFormTokenOutcome.InvalidRequest, null, errorMessage);

    public static TaxFormTokenResult ServerError(string errorMessage) =>
        new(TaxFormTokenOutcome.ServerError, null, errorMessage);
}

/// <summary>
/// Issues the short-lived TaxBandits transient token that the W-9/W-8 Drop-in UI needs.
///
/// <para>
/// This exists as a service rather than only as a controller action because
/// <c>/submittaxform</c> is <c>@rendermode InteractiveServer</c>: its
/// <c>OnAfterRenderAsync</c> runs inside the SignalR circuit, whose DI scope has no
/// <c>HttpContext</c>. The shared <c>HttpClient</c> in <c>Program.cs</c> forwards the auth cookie
/// only when a request scope exists, so the page's self-call to <c>api/creator/tax-form-token</c>
/// always arrived anonymous and always 401'd. Calling the server in-process is the house pattern -
/// see the note on <c>SongPlayerInteractive</c> about the server asking itself a question it can
/// answer directly.
/// </para>
/// </summary>
public interface ITaxFormTokenService
{
    /// <summary>
    /// Issues a transient token for the given user's pending tax form.
    /// </summary>
    /// <param name="userId">The application user's id.</param>
    /// <param name="userEmail">
    /// Fallback payee reference, used when the creator has no stored TaxBandits payee ref.
    /// </param>
    Task<TaxFormTokenResult> GetTaxFormTokenAsync(
        int userId,
        string? userEmail,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration and token payload the TaxBandits Drop-in UI needs to render.
/// </summary>
public class TaxFormTokenResponse
{
    public bool Success { get; set; }
    public string? TransientToken { get; set; }
    public string? PayeeRef { get; set; }
    public string? BusinessId { get; set; }
    public string? ScriptUrl { get; set; }
    public string? ErrorMessage { get; set; }
}
