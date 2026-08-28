using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MusicSalesApp.Services;

/// <summary>
/// Validates the identity token produced by the native iOS Sign in with Apple sheet.
/// </summary>
/// <remarks>
/// Unlike Google - where the stock ASP.NET handler performs the whole OAuth dance server-side -
/// the Apple flow is native on the device, so the app hands us a finished JWT and all we do is
/// verify it. Signing keys come from Apple's JWKS via <see cref="ConfigurationManager{T}"/>,
/// which caches them and re-fetches on rotation; do not hand-roll that fetch.
/// </remarks>
public sealed class AppleIdentityTokenValidator : IAppleIdentityTokenValidator
{
    public const string AppleIssuer = "https://appleid.apple.com";
    private const string AppleMetadataAddress = "https://appleid.apple.com/.well-known/openid-configuration";

    private readonly string _bundleId;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly ILogger<AppleIdentityTokenValidator> _logger;

    public AppleIdentityTokenValidator(
        IConfiguration configuration,
        ILogger<AppleIdentityTokenValidator> logger)
        // The audience of an Apple identity token from a native iOS sign-in is the app's bundle
        // id, which AppleAppStore:BundleId already carries in every environment for receipt
        // verification. Fall back to it so no environment needs new config, while still allowing
        // an explicit Authentication:Apple:BundleId override.
        : this(
            FirstNonBlank(
                configuration["Authentication:Apple:BundleId"],
                configuration["AppleAppStore:BundleId"]),
            new ConfigurationManager<OpenIdConnectConfiguration>(
                AppleMetadataAddress,
                new OpenIdConnectConfigurationRetriever()),
            logger)
    {
    }

    // Test seam: lets the suite supply canned signing keys instead of reaching out to Apple.
    public AppleIdentityTokenValidator(
        string bundleId,
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        ILogger<AppleIdentityTokenValidator> logger)
    {
        _bundleId = bundleId;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    private static string FirstNonBlank(string preferred, string fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback ?? string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_bundleId);

    public async Task<(bool Success, AppleIdentityTokenPayload Payload, string Error)> ValidateAsync(
        string identityToken,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return (false, null, "Sign in with Apple is not configured.");
        }

        if (string.IsNullOrWhiteSpace(identityToken))
        {
            return (false, null, "Apple identity token is required.");
        }

        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to retrieve Apple signing keys");
            return (false, null, "Unable to verify Apple sign-in right now. Please try again.");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = AppleIssuer,
            ValidateAudience = true,
            ValidAudience = _bundleId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
        };

        // MapInboundClaims=false keeps the raw Apple claim names ("sub", "email") rather than
        // rewriting them to the long ClaimTypes.* URIs.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        try
        {
            var principal = handler.ValidateToken(identityToken, parameters, out _);

            var subject = principal.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(subject))
            {
                return (false, null, "Apple sign-in did not provide a user identifier.");
            }

            return (true, new AppleIdentityTokenPayload(
                subject,
                principal.FindFirst("email")?.Value ?? string.Empty,
                ReadAppleBoolean(principal.FindFirst("email_verified")?.Value, defaultValue: true),
                ReadAppleBoolean(principal.FindFirst("is_private_email")?.Value, defaultValue: false)),
                string.Empty);
        }
        catch (SecurityTokenExpiredException)
        {
            return (false, null, "Apple sign-in has expired. Please try again.");
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Apple identity token failed validation");
            return (false, null, "Apple sign-in could not be verified.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error validating Apple identity token");
            return (false, null, "Apple sign-in could not be verified.");
        }
    }

    // Apple is inconsistent about whether these arrive as JSON booleans or as quoted strings,
    // so accept either rather than trusting one shape.
    private static bool ReadAppleBoolean(string value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}
