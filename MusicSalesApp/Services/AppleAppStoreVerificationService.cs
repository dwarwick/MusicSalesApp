using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

public class AppleAppStoreVerificationService : IAppleAppStoreVerificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleAppStoreVerificationService> _logger;
    private readonly string _bundleId;
    private readonly string _issuerId;
    private readonly string _keyId;
    private readonly string _apiBaseUrl;
    private readonly ECDsa _privateKey;
    private readonly string _initializationError;

    internal static string ResolvePrivateKeyPath(string configuredPath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }

    internal static string DescribeCredentialConfigurationIssue(
        string privateKeyPath,
        string inlinePrivateKey,
        string issuerId,
        string keyId,
        string bundleId)
    {
        if (!string.IsNullOrWhiteSpace(privateKeyPath) && !File.Exists(privateKeyPath))
        {
            return "Configured Apple App Store private key file was not found on the server.";
        }

        if (string.IsNullOrWhiteSpace(privateKeyPath) && string.IsNullOrWhiteSpace(inlinePrivateKey))
        {
            return "Apple App Store private key is not configured on the server.";
        }

        if (string.IsNullOrWhiteSpace(issuerId) || string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(bundleId))
        {
            return "Apple App Store verification configuration is incomplete on the server.";
        }

        return "Apple App Store private key could not be loaded on the server.";
    }

    internal static AppleSignedTransactionPayload DecodeSignedTransactionInfo(string signedTransactionInfo)
    {
        return DecodeSignedPayload<AppleSignedTransactionPayload>(
            signedTransactionInfo,
            "Apple App Store did not return signed transaction info.",
            "Apple App Store returned malformed signed transaction info.",
            "Apple App Store returned an empty transaction payload.",
            "Failed to parse Apple App Store transaction data.");
    }

    internal static AppleServerNotificationPayload DecodeServerNotificationPayload(string signedPayload)
    {
        return DecodeSignedPayload<AppleServerNotificationPayload>(
            signedPayload,
            "Apple App Store did not return a signed notification payload.",
            "Apple App Store returned malformed signed notification payload.",
            "Apple App Store returned an empty notification payload.",
            "Failed to parse Apple App Store notification data.");
    }

    internal static string DetermineSubscriptionStatus(DateTime utcNow, DateTime? expiryTimeUtc, DateTime? revocationTimeUtc)
    {
        if (revocationTimeUtc.HasValue)
        {
            return SubscriptionStatuses.Cancelled;
        }

        if (expiryTimeUtc.HasValue && expiryTimeUtc.Value <= utcNow)
        {
            return SubscriptionStatuses.Expired;
        }

        return SubscriptionStatuses.Active;
    }

    internal static string DetermineNotificationStatus(
        string notificationType,
        string subtype,
        DateTime utcNow,
        DateTime? expiryTimeUtc,
        DateTime? revocationTimeUtc)
    {
        switch (notificationType)
        {
            case "DID_FAIL_TO_RENEW":
                return SubscriptionStatuses.Suspended;
            case "EXPIRED":
            case "GRACE_PERIOD_EXPIRED":
                return SubscriptionStatuses.Expired;
            case "REVOKE":
            case "REFUND":
                return SubscriptionStatuses.Cancelled;
            case "DID_RENEW":
            case "SUBSCRIBED":
            case "OFFER_REDEEMED":
            case "RENEWAL_EXTENDED":
            case "RENEWAL_EXTENSION":
                return SubscriptionStatuses.Active;
        }

        return DetermineSubscriptionStatus(utcNow, expiryTimeUtc, revocationTimeUtc);
    }

    private static T DecodeSignedPayload<T>(
        string signedPayload,
        string missingPayloadMessage,
        string malformedPayloadMessage,
        string emptyPayloadMessage,
        string parseFailureMessage)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(signedPayload))
        {
            throw new AppleAppStoreVerificationException(missingPayloadMessage);
        }

        var segments = signedPayload.Split('.');
        if (segments.Length < 2)
        {
            throw new AppleAppStoreVerificationException(malformedPayloadMessage);
        }

        try
        {
            var payloadJson = Base64UrlEncoder.Decode(segments[1]);
            var payload = JsonSerializer.Deserialize<T>(payloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload == null)
            {
                throw new AppleAppStoreVerificationException(emptyPayloadMessage);
            }

            return payload;
        }
        catch (AppleAppStoreVerificationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AppleAppStoreVerificationException(parseFailureMessage, ex);
        }
    }

    public AppleAppStoreVerificationService(
        IConfiguration configuration,
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<AppleAppStoreVerificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _bundleId = configuration["AppleAppStore:BundleId"];
        _issuerId = configuration["AppleAppStore:IssuerId"];
        _keyId = configuration["AppleAppStore:KeyId"];
        _apiBaseUrl = configuration["AppleAppStore:ApiBaseUrl"] ?? "https://api.storekit.itunes.apple.com";

        var privateKeyPath = ResolvePrivateKeyPath(configuration["AppleAppStore:PrivateKeyPath"], environment.ContentRootPath);
        var inlinePrivateKey = configuration["AppleAppStore:PrivateKeyPem"];
        _initializationError = DescribeCredentialConfigurationIssue(privateKeyPath, inlinePrivateKey, _issuerId, _keyId, _bundleId);

        try
        {
            var privateKeyPem = !string.IsNullOrWhiteSpace(privateKeyPath) && File.Exists(privateKeyPath)
                ? File.ReadAllText(privateKeyPath)
                : inlinePrivateKey;

            if (!string.IsNullOrWhiteSpace(privateKeyPem))
            {
                _privateKey = ECDsa.Create();
                _privateKey.ImportFromPem(privateKeyPem);
            }
            else
            {
                _logger.LogWarning("Apple App Store verification key not available. {InitializationError}", _initializationError);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apple App Store private key not available. {InitializationError}", _initializationError);
        }
    }

    public async Task<AppleAppStoreSubscriptionInfo> VerifySubscriptionAsync(string transactionId, string productId)
    {
        if (_privateKey == null)
        {
            _logger.LogError("Cannot verify Apple subscription — service not initialized. {InitializationError}", _initializationError);
            throw new AppleAppStoreVerificationException(_initializationError ?? "Apple App Store verification is not configured on the server.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiBaseUrl.TrimEnd('/')}/inApps/v1/transactions/{Uri.EscapeDataString(transactionId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateBearerToken());

            var client = _httpClientFactory.CreateClient();
            var response = await client.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AppleAppStoreVerificationException("Apple App Store could not find this transaction for the configured app.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new AppleAppStoreVerificationException("Apple App Store API access was denied. Check the issuer ID, key ID, private key, and App Store Connect permissions.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Apple App Store verification failed with status {StatusCode}. Body: {Body}",
                    response.StatusCode,
                    errorBody);
                throw new AppleAppStoreVerificationException("Apple App Store verification failed on the server.");
            }

            var lookupResponse = await response.Content.ReadFromJsonAsync<AppleTransactionLookupResponse>();
            var payload = DecodeSignedTransactionInfo(lookupResponse?.SignedTransactionInfo);
            ValidatePayload(payload, transactionId, productId);

            var expiryTime = payload.ExpiresDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(payload.ExpiresDate.Value).UtcDateTime
                : (DateTime?)null;
            var revocationTime = payload.RevocationDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(payload.RevocationDate.Value).UtcDateTime
                : (DateTime?)null;

            return new AppleAppStoreSubscriptionInfo(
                DetermineSubscriptionStatus(DateTime.UtcNow, expiryTime, revocationTime),
                expiryTime,
                payload.TransactionId,
                payload.OriginalTransactionId,
                payload.ProductId,
                payload.Environment,
                payload.AppAccountToken);
        }
        catch (AppleAppStoreVerificationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Apple App Store subscription");
            throw new AppleAppStoreVerificationException("Apple App Store verification failed on the server.", ex);
        }
    }

    private string CreateBearerToken()
    {
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Issuer = _issuerId,
            Audience = "appstoreconnect-v1",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new ECDsaSecurityKey(_privateKey) { KeyId = _keyId },
                SecurityAlgorithms.EcdsaSha256)
        };

        return new JsonWebTokenHandler().CreateToken(tokenDescriptor);
    }

    private void ValidatePayload(AppleSignedTransactionPayload payload, string transactionId, string productId)
    {
        if (!string.Equals(payload.BundleId, _bundleId, StringComparison.Ordinal))
        {
            throw new AppleAppStoreVerificationException("Apple App Store transaction bundle ID does not match the configured app.");
        }

        if (!string.Equals(payload.ProductId, productId, StringComparison.Ordinal))
        {
            throw new AppleAppStoreVerificationException("Apple App Store transaction product ID does not match the configured subscription.");
        }

        if (!string.Equals(payload.TransactionId, transactionId, StringComparison.Ordinal))
        {
            throw new AppleAppStoreVerificationException("Apple App Store returned a different transaction than the one requested.");
        }
    }

    internal sealed class AppleTransactionLookupResponse
    {
        public string SignedTransactionInfo { get; set; }
    }

    internal sealed class AppleSignedTransactionPayload
    {
        public string TransactionId { get; set; }
        public string OriginalTransactionId { get; set; }
        public string ProductId { get; set; }
        public string BundleId { get; set; }
        public string Environment { get; set; }
        public string AppAccountToken { get; set; }
        public long? ExpiresDate { get; set; }
        public long? RevocationDate { get; set; }
    }

    internal sealed class AppleServerNotificationPayload
    {
        public string NotificationType { get; set; }
        public string Subtype { get; set; }
        public AppleServerNotificationData Data { get; set; }
    }

    internal sealed class AppleServerNotificationData
    {
        public string SignedTransactionInfo { get; set; }
        public string SignedRenewalInfo { get; set; }
    }
}