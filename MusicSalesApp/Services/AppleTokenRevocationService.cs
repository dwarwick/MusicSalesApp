using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public sealed class AppleTokenRevocationService : IAppleTokenRevocationService, IDisposable
{
    private const string AppleTokenEndpoint = "https://appleid.apple.com/auth/token";
    private const string AppleRevokeEndpoint = "https://appleid.apple.com/auth/revoke";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleTokenRevocationService> _logger;
    private readonly string _teamId;
    private readonly string _clientId;
    private readonly string _keyId;
    private readonly ECDsa _privateKey;

    public AppleTokenRevocationService(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<AppleTokenRevocationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _teamId = configuration["Authentication:Apple:TeamId"] ?? string.Empty;
        _keyId = configuration["Authentication:Apple:KeyId"] ?? string.Empty;

        // The client id for a native app is the bundle id - the same value the identity token is
        // audienced to, so it falls back the same way.
        _clientId = FirstNonBlank(
            configuration["Authentication:Apple:BundleId"],
            configuration["AppleAppStore:BundleId"]);

        // This is a Sign in with Apple key, NOT the App Store Connect key next to it in
        // App_Data/Secrets - they are separate keys with separate key ids.
        var privateKeyPem = configuration["Authentication:Apple:PrivateKeyPem"];
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            var path = AppleAppStoreVerificationService.ResolvePrivateKeyPath(
                NormalizeSeparators(configuration["Authentication:Apple:PrivateKeyPath"]),
                environment.ContentRootPath);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                privateKeyPem = File.ReadAllText(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(privateKeyPem))
        {
            try
            {
                // Reuses the loader with the BouncyCastle fallback that the App Store key needs on
                // the IIS host.
                _privateKey = AppleAppStoreVerificationService.LoadPrivateKey(privateKeyPem, out var strategy);
                _logger.LogInformation("Loaded the Sign in with Apple private key using {Strategy}", strategy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the Sign in with Apple private key");
            }
        }
    }

    public bool IsConfigured =>
        _privateKey is not null
        && !string.IsNullOrWhiteSpace(_teamId)
        && !string.IsNullOrWhiteSpace(_keyId)
        && !string.IsNullOrWhiteSpace(_clientId);

    public async Task<string> ExchangeAuthorizationCodeAsync(
        string authorizationCode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(authorizationCode))
        {
            return null;
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = CreateClientSecret(),
            ["code"] = authorizationCode,
            ["grant_type"] = "authorization_code"
        };

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.PostAsync(
                AppleTokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Apple authorization-code exchange failed ({Status}): {Body}", response.StatusCode, body);
                return null;
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("refresh_token", out var refreshToken)
                ? refreshToken.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apple authorization-code exchange threw");
            return null;
        }
    }

    public async Task<bool> RevokeRefreshTokenAsync(
        string refreshToken, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = CreateClientSecret(),
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token"
        };

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.PostAsync(
                AppleRevokeEndpoint, new FormUrlEncodedContent(form), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Apple token revocation failed ({Status}): {Body}", response.StatusCode, body);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apple token revocation threw");
            return false;
        }
    }

    /// <summary>
    /// Apple's "client secret" is a short-lived ES256 JWT rather than a fixed string, which is why
    /// it is minted per call instead of read from configuration.
    /// </summary>
    private string CreateClientSecret()
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _teamId,
            Audience = "https://appleid.apple.com",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = _clientId },
            Subject = new ClaimsIdentity(),
            SigningCredentials = new SigningCredentials(
                new ECDsaSecurityKey(_privateKey) { KeyId = _keyId },
                SecurityAlgorithms.EcdsaSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// The configured path uses Windows separators to match the sibling AppleAppStore key on the
    /// IIS host. A no-op there, and what makes the same value resolve when the API is run on
    /// macOS or Linux.
    /// </summary>
    private static string NormalizeSeparators(string path)
        => string.IsNullOrWhiteSpace(path)
            ? path
            : path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static string FirstNonBlank(string preferred, string fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback ?? string.Empty;

    public void Dispose() => _privateKey?.Dispose();
}
