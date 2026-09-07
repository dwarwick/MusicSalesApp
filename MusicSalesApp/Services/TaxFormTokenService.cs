#nullable enable

using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc cref="ITaxFormTokenService"/>
public class TaxFormTokenService : ITaxFormTokenService
{
    private readonly ICreatorService _creatorService;
    private readonly ITaxBanditsService _taxBanditsService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TaxFormTokenService> _logger;

    public TaxFormTokenService(
        ICreatorService creatorService,
        ITaxBanditsService taxBanditsService,
        IConfiguration configuration,
        ILogger<TaxFormTokenService> logger)
    {
        _creatorService = creatorService;
        _taxBanditsService = taxBanditsService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TaxFormTokenResult> GetTaxFormTokenAsync(
        int userId,
        string? userEmail,
        CancellationToken cancellationToken = default)
    {
        var creator = await _creatorService.GetCreatorByUserIdAsync(userId);
        if (creator == null)
        {
            return TaxFormTokenResult.InvalidRequest(
                "Creator record not found. Please start the onboarding process first.");
        }

        // Tax form token is only available when status is Pending
        if (creator.TaxFormStatus != TaxFormStatus.Pending)
        {
            return TaxFormTokenResult.InvalidRequest(
                "No pending tax form request. Please initiate a tax form submission first.");
        }

        // Get the PayeeRef (email) used in the original request
        var payeeRef = creator.TaxBanditsPayeeRef ?? userEmail;
        if (string.IsNullOrWhiteSpace(payeeRef))
        {
            return TaxFormTokenResult.InvalidRequest("No email address found for tax form request.");
        }

        try
        {
            var origins = ResolveAllowedOrigins();

            _logger.LogInformation("Configured origins for TaxBandits: {Origins}", string.Join(", ", origins));

            if (origins.Count == 0)
            {
                _logger.LogError("No origins configured in Fido2:Origins for TaxBandits Drop-in UI. " +
                    "Set as indexed env vars (Fido2:Origins:0, Fido2:Origins:1) or comma-separated (Fido2:Origins=url1,url2)");
                return TaxFormTokenResult.ServerError(
                    "Server configuration error: no allowed origins configured.");
            }

            var tokenResult = await _taxBanditsService.GetTransientTokenAsync(origins, cancellationToken);

            if (!tokenResult.Success)
            {
                _logger.LogError("Failed to get transient token for user {UserId}: {Error}", userId, tokenResult.ErrorMessage);
                return TaxFormTokenResult.ServerError(
                    $"Failed to initialize tax form: {tokenResult.ErrorMessage}");
            }

            var businessId = _configuration["TaxBandits:BusinessId"];
            var scriptUrl = _configuration["TaxBandits:ScriptUrl"];

            _logger.LogInformation("Tax form token generated for user {UserId}. BusinessId: {BusinessId}, TokenLength: {TokenLength}",
                userId, businessId, tokenResult.TransientToken?.Length ?? 0);

            return TaxFormTokenResult.Success(new TaxFormTokenResponse
            {
                Success = true,
                TransientToken = tokenResult.TransientToken,
                PayeeRef = payeeRef,
                BusinessId = businessId,
                ScriptUrl = scriptUrl
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while getting tax form token for user {UserId}", userId);
            return TaxFormTokenResult.ServerError("An error occurred while preparing the tax form.");
        }
    }

    /// <summary>
    /// Reads the origins TaxBandits will accept the Drop-in UI from. Three shapes are supported,
    /// because the value arrives differently per host:
    /// <list type="number">
    /// <item>JSON array in appsettings.json: <c>"Origins": ["https://example.com"]</c></item>
    /// <item>Indexed environment variables: <c>Fido2:Origins:0</c>, <c>Fido2:Origins:1</c></item>
    /// <item>Comma-separated string: <c>Fido2:Origins = "https://a.com,https://b.com"</c></item>
    /// </list>
    /// </summary>
    private List<string> ResolveAllowedOrigins()
    {
        var origins = _configuration.GetSection("Fido2:Origins").Get<List<string>>() ?? new List<string>();

        // Fallback: check if origins is empty but a comma-separated string was provided
        if (origins.Count == 0)
        {
            var originsString = _configuration["Fido2:Origins"];
            if (!string.IsNullOrWhiteSpace(originsString))
            {
                origins = originsString
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                _logger.LogInformation("Parsed {Count} origins from comma-separated string", origins.Count);
            }
        }

        return origins;
    }
}
