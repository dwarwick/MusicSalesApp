#nullable enable
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// iOS delivery, straight to APNs over HTTP/2.
/// </summary>
/// <remarks>
/// <para>
/// No SDK and no Firebase relay. The signing is the same ES256-over-a-.p8 shape as
/// <see cref="AppleTokenRevocationService"/>, and it reuses that file's key loader - including the
/// BouncyCastle fallback the IIS host needs.
/// </para>
/// <para>
/// <b>This is a THIRD Apple key.</b> It is an APNs Auth Key, not the Sign in with Apple key and not
/// the App Store Connect key already in <c>App_Data/Secrets</c>. All three are .p8 files with their
/// own key ids, they are not interchangeable, and using the wrong one fails with a 403 that says
/// only "InvalidProviderToken".
/// </para>
/// </remarks>
public sealed class ApplePushNotificationSender : IPushNotificationSender, IDisposable
{
    private const string ProductionHost = "https://api.push.apple.com";
    private const string SandboxHost = "https://api.sandbox.push.apple.com";
    private const int MaxConcurrency = 8;

    /// <summary>
    /// Apple rejects provider tokens refreshed more often than once every 20 minutes, and expires
    /// them after 60. Refreshing at 45 sits clear of both ends.
    /// </summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(45);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApplePushNotificationSender> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ECDsa? _privateKey;
    private readonly string _teamId;
    private readonly string _keyId;
    private readonly string _bundleId;
    private readonly string _host;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiry;

    public string Platform => PushPlatforms.Ios;

    public bool IsConfigured =>
        _privateKey is not null
        && !string.IsNullOrWhiteSpace(_teamId)
        && !string.IsNullOrWhiteSpace(_keyId)
        && !string.IsNullOrWhiteSpace(_bundleId);

    public ApplePushNotificationSender(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<ApplePushNotificationSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _logger = logger;

        _teamId = configuration["Push:Apple:TeamId"]
                  ?? configuration["Authentication:Apple:TeamId"]
                  ?? string.Empty;
        _keyId = configuration["Push:Apple:KeyId"] ?? string.Empty;

        // The apns-topic is the app's bundle id, which is the same value the identity token is
        // audienced to - so it falls back the same way the revocation service's client id does.
        _bundleId = FirstNonBlank(
            configuration["Push:Apple:BundleId"],
            FirstNonBlank(
                configuration["Authentication:Apple:BundleId"],
                configuration["AppleAppStore:BundleId"]));

        // Sandbox tokens are rejected by production and vice versa, with an unhelpful
        // "BadDeviceToken" either way - so this follows the environment rather than being guessed.
        var useSandbox = configuration.GetValue("Push:Apple:UseSandbox", environment.IsDevelopment());
        _host = useSandbox ? SandboxHost : ProductionHost;

        var privateKeyPem = configuration["Push:Apple:PrivateKeyPem"];

        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            var path = AppleAppStoreVerificationService.ResolvePrivateKeyPath(
                NormalizeSeparators(configuration["Push:Apple:PrivateKeyPath"]),
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
                _privateKey = AppleAppStoreVerificationService.LoadPrivateKey(privateKeyPem, out var strategy);
                _logger.LogInformation("Loaded the APNs auth key using {Strategy}.", strategy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the APNs auth key; iOS push is disabled.");
            }
        }

        if (!IsConfigured)
        {
            _logger.LogInformation(
                "iOS push is not configured (Push:Apple:TeamId, KeyId, BundleId and an APNs auth key are required).");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PushDeliveryResult>> SendAsync(
        PushMessage message,
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken = default)
    {
        if (tokens is null || tokens.Count == 0)
        {
            return [];
        }

        if (!IsConfigured)
        {
            return tokens
                .Select(token => new PushDeliveryResult(
                    token, PushDeliveryOutcome.TransportFailure, "Apple push is not configured."))
                .ToList();
        }

        string providerToken;

        try
        {
            providerToken = await GetProviderTokenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mint an APNs provider token; deferring {Count} pushes.", tokens.Count);

            return tokens
                .Select(token => new PushDeliveryResult(
                    token, PushDeliveryOutcome.TransportFailure, "No APNs provider token."))
                .ToList();
        }

        var payload = BuildPayload(message);
        var gate = new SemaphoreSlim(MaxConcurrency);

        var sends = tokens.Select(async token =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                return await SendOneAsync(providerToken, payload, token, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(sends);
    }

    private async Task<PushDeliveryResult> SendOneAsync(
        string providerToken,
        string payload,
        string deviceToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host}/3/device/{deviceToken}")
            {
                // APNs speaks HTTP/2 only, and .NET will happily negotiate down to 1.1 unless told
                // otherwise - which APNs answers by closing the connection rather than erroring.
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            request.Headers.TryAddWithoutValidation("authorization", $"bearer {providerToken}");
            request.Headers.TryAddWithoutValidation("apns-topic", _bundleId);
            request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            request.Headers.TryAddWithoutValidation("apns-priority", "10");

            using var response = await client.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new PushDeliveryResult(deviceToken, PushDeliveryOutcome.Delivered);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ClassifyFailure(deviceToken, response.StatusCode, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "APNs request threw.");
            return new PushDeliveryResult(deviceToken, PushDeliveryOutcome.TransportFailure, ex.Message);
        }
    }

    /// <summary>
    /// Maps an APNs rejection onto one of the three outcomes.
    /// </summary>
    /// <remarks>
    /// The reason string carries the meaning, not the status code. In particular <c>410 Unregistered</c>
    /// and <c>400 BadDeviceToken</c> are the only two that justify dropping a row - and
    /// <c>ExpiredProviderToken</c> looks like an auth failure but is ours to fix by re-minting, so
    /// it must stay retryable rather than blaming the device.
    /// </remarks>
    private PushDeliveryResult ClassifyFailure(string deviceToken, HttpStatusCode status, string body)
    {
        var reason = TryReadReason(body) ?? status.ToString();

        if (status == HttpStatusCode.Gone
            || string.Equals(reason, "Unregistered", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "BadDeviceToken", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "DeviceTokenNotForTopic", StringComparison.OrdinalIgnoreCase))
        {
            return new PushDeliveryResult(deviceToken, PushDeliveryOutcome.TokenRejected, reason);
        }

        if (string.Equals(reason, "ExpiredProviderToken", StringComparison.OrdinalIgnoreCase))
        {
            // Ours to fix. Drop the cache so the next batch mints a fresh one.
            InvalidateProviderToken();
            return new PushDeliveryResult(deviceToken, PushDeliveryOutcome.TransportFailure, reason);
        }

        if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
        {
            return new PushDeliveryResult(deviceToken, PushDeliveryOutcome.TransportFailure, reason);
        }

        _logger.LogWarning("APNs refused a push ({Status}): {Reason}", status, reason);
        return new PushDeliveryResult(deviceToken, PushDeliveryOutcome.PermanentFailure, reason);
    }

    private static string? TryReadReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> GetProviderTokenAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        if (_cachedToken is not null && now < _cachedTokenExpiry)
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);

        try
        {
            // Re-check inside the lock: a whole batch can arrive at an expired token at once, and
            // minting one per caller is precisely what Apple's refresh limit punishes.
            now = _timeProvider.GetUtcNow();

            if (_cachedToken is not null && now < _cachedTokenExpiry)
            {
                return _cachedToken;
            }

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = _teamId,
                IssuedAt = now.UtcDateTime,
                Subject = new ClaimsIdentity(),
                SigningCredentials = new SigningCredentials(
                    new ECDsaSecurityKey(_privateKey) { KeyId = _keyId },
                    SecurityAlgorithms.EcdsaSha256),
            };

            _cachedToken = new JsonWebTokenHandler().CreateToken(descriptor);
            _cachedTokenExpiry = now.Add(TokenLifetime);

            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void InvalidateProviderToken()
    {
        _cachedToken = null;
        _cachedTokenExpiry = default;
    }

    /// <summary>
    /// The APNs payload.
    /// </summary>
    /// <remarks>
    /// The alert block is what iOS shows while the app is backgrounded. Custom keys sit alongside
    /// <c>aps</c> at the top level, not inside it - APNs rejects unknown keys within <c>aps</c>.
    /// </remarks>
    private static string BuildPayload(PushMessage message)
    {
        var payload = new Dictionary<string, object>
        {
            ["aps"] = new Dictionary<string, object>
            {
                ["alert"] = new Dictionary<string, object>
                {
                    ["title"] = message.Title,
                    ["body"] = message.Body,
                },
                ["sound"] = "default",
            },
        };

        if (message.Data is not null)
        {
            foreach (var pair in message.Data)
            {
                payload[pair.Key] = pair.Value;
            }
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string NormalizeSeparators(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? path ?? string.Empty
            : path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static string FirstNonBlank(string? preferred, string? fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback ?? string.Empty;

    public void Dispose()
    {
        _privateKey?.Dispose();
        _tokenLock.Dispose();
    }
}
