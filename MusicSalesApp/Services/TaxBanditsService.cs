#nullable enable
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for TaxBandits OAuth authentication operations.
/// </summary>
public sealed class TaxBanditsService : ITaxBanditsService
{
    private readonly HttpClient _http;
    private readonly ILogger<TaxBanditsService> _logger;
    private readonly IConfiguration _configuration;

    public TaxBanditsService(HttpClient http, ILogger<TaxBanditsService> logger, IConfiguration configuration)
    {
        _http = http;
        _logger = logger;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public async Task<TaxBanditsAuthResponse> GetAccessTokenAsync(
        string clientId,
        string userToken,
        string clientSecret,
        bool useSandbox = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("clientId is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(userToken)) throw new ArgumentException("userToken is required.", nameof(userToken));
        if (string.IsNullOrWhiteSpace(clientSecret)) throw new ArgumentException("clientSecret is required.", nameof(clientSecret));

        _logger.LogInformation("Requesting TaxBandits access token for clientId: {ClientId}", clientId);

        // 1) Build JWS (HS256)
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var jws = CreateJwsHs256(clientId, userToken, clientSecret, iat);

        // 2) Get URL from configuration or use defaults
        var url = useSandbox
            ? _configuration["TaxBandits:SandboxUrl"] ?? "https://testoauth.expressauth.net/v2/tbsauth"
            : _configuration["TaxBandits:ProductionUrl"] ?? "https://oauth.expressauth.net/v2/tbsauth";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // TaxBandits requires this exact header name:
        req.Headers.TryAddWithoutValidation("Authentication", jws);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Helpful error detail if status != 200
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("TaxBandits auth failed: {StatusCode} {ReasonPhrase}. Body: {Body}", 
                (int)resp.StatusCode, resp.ReasonPhrase, body);
            throw new HttpRequestException(
                $"TaxBandits auth failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }

        var result = JsonSerializer.Deserialize<TaxBanditsAuthResponse>(body, JsonOptions)
                     ?? throw new InvalidOperationException("Failed to deserialize TaxBandits auth response.");

        _logger.LogInformation("TaxBandits access token obtained successfully");
        return result;
    }

    /// <summary>
    /// Creates the JWS: base64url(header).base64url(payload).base64url(HMACSHA256(signingInput, clientSecret))
    /// </summary>
    public static string CreateJwsHs256(string clientId, string userToken, string clientSecret, long iatUnixSeconds)
    {
        // Header and payload exactly per TaxBandits doc
        var headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var payloadJson = JsonSerializer.Serialize(new
        {
            iss = clientId,
            sub = clientId,
            aud = userToken,
            iat = iatUnixSeconds
        }, JsonOptions);

        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var signingInput = $"{header}.{payload}";
        var signatureBytes = HmacSha256(Encoding.UTF8.GetBytes(signingInput), Encoding.UTF8.GetBytes(clientSecret));
        var signature = Base64UrlEncode(signatureBytes);

        return $"{signingInput}.{signature}";
    }

    private static byte[] HmacSha256(byte[] data, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        // Base64Url encoding: no padding, + -> -, / -> _
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
