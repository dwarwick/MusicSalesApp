using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MusicSalesApp.Common.Helpers;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace MusicSalesApp.Services;

public class AppleAppStoreVerificationService : IAppleAppStoreVerificationService
{
    private static readonly HashSet<string> AppleRootSha256Fingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        // Published by Apple for Apple Root CA - G2 and Apple Root CA - G3.
        "C2B9B042DD57830E7D117DAC55AC8AE19407D38E41D88F3215BC3A890444A050",
        "63343ABFB89A6A03EBB57E9B3F5FA7BE7C4F5C756F3017B3A8C488C3653E9179"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleAppStoreVerificationService> _logger;
    private readonly string _bundleId;
    private readonly string _issuerId;
    private readonly string _keyId;
    private readonly string _apiBaseUrl;
    private readonly string _sandboxApiBaseUrl;
    private readonly string _privateKeyPath;
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

    internal static string DescribeApiAccessDenied(HttpStatusCode statusCode, string errorBody)
    {
        const string baseMessage = "Apple App Store API access was denied. Check the issuer ID, key ID, private key, and App Store Connect permissions.";

        if (string.IsNullOrWhiteSpace(errorBody))
        {
            return baseMessage;
        }

        try
        {
            using var document = JsonDocument.Parse(errorBody);
            var root = document.RootElement;

            string errorCode = null;
            string errorMessage = null;

            if (root.TryGetProperty("errorCode", out var errorCodeElement))
            {
                errorCode = errorCodeElement.ValueKind == JsonValueKind.String
                    ? errorCodeElement.GetString()
                    : errorCodeElement.GetRawText();
            }

            if (root.TryGetProperty("errorMessage", out var errorMessageElement))
            {
                errorMessage = errorMessageElement.GetString();
            }

            if (!string.IsNullOrWhiteSpace(errorCode) || !string.IsNullOrWhiteSpace(errorMessage))
            {
                var detail = string.Join(" - ", new[] { errorCode, errorMessage }.Where(value => !string.IsNullOrWhiteSpace(value)));
                return $"{baseMessage} Apple response ({(int)statusCode}): {detail}";
            }
        }
        catch (JsonException)
        {
        }

        return $"{baseMessage} Apple response ({(int)statusCode}): {errorBody}";
    }

    internal static string DescribeBearerToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "<empty token>";
        }

        try
        {
            var jwt = new JsonWebToken(token);
            var keyId = jwt.Kid ?? "<missing>";
            var issuer = jwt.Issuer ?? "<missing>";
            var audience = jwt.Audiences.FirstOrDefault() ?? "<missing>";
            var bundleId = jwt.Claims.FirstOrDefault(claim => claim.Type == "bid")?.Value ?? "<missing>";
            var expiresAt = jwt.ValidTo == DateTime.MinValue
                ? "<missing>"
                : jwt.ValidTo.ToString("O");

            return $"kid={keyId}; iss={issuer}; aud={audience}; bid={bundleId}; exp={expiresAt}";
        }
        catch (Exception ex)
        {
            return $"<unparseable token: {ex.GetType().Name}>";
        }
    }

    internal static ECDsa LoadPrivateKey(string privateKeyPem, out string loadStrategy)
    {
        try
        {
            var privateKey = ECDsa.Create();
            privateKey.ImportFromPem(privateKeyPem);
            loadStrategy = "ImportFromPem";
            return privateKey;
        }
        catch (Exception primaryException)
        {
            try
            {
                using var stringReader = new StringReader(privateKeyPem);
                var pemReader = new PemReader(stringReader);
                var keyObject = pemReader.ReadObject();

                var ecPrivateKey = keyObject switch
                {
                    AsymmetricCipherKeyPair keyPair => keyPair.Private as ECPrivateKeyParameters,
                    ECPrivateKeyParameters privateKeyParameters => privateKeyParameters,
                    _ => null
                };

                if (ecPrivateKey == null)
                {
                    throw new CryptographicException("The PEM file did not contain an EC private key.");
                }

                loadStrategy = "BouncyCastle";
                return CreateFromEcPrivateKeyParameters(ecPrivateKey);
            }
            catch (Exception fallbackException)
            {
                throw new AggregateException(primaryException, fallbackException);
            }
        }
    }

    internal static ECDsa CreateFromEcPrivateKeyParameters(ECPrivateKeyParameters ecPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(ecPrivateKey);

        var publicPoint = ecPrivateKey.Parameters.G.Multiply(ecPrivateKey.D).Normalize();
        var fieldSizeBytes = (ecPrivateKey.Parameters.Curve.FieldSize + 7) / 8;

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = PadToSize(ecPrivateKey.D.ToByteArrayUnsigned(), fieldSizeBytes),
            Q = new ECPoint
            {
                X = PadToSize(publicPoint.AffineXCoord.ToBigInteger().ToByteArrayUnsigned(), fieldSizeBytes),
                Y = PadToSize(publicPoint.AffineYCoord.ToBigInteger().ToByteArrayUnsigned(), fieldSizeBytes)
            }
        };

        return ECDsa.Create(parameters);
    }

    internal static byte[] PadToSize(byte[] value, int size)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == size)
        {
            return value;
        }

        if (value.Length > size)
        {
            return value[^size..];
        }

        var padded = new byte[size];
        Buffer.BlockCopy(value, 0, padded, size - value.Length, value.Length);
        return padded;
    }

    internal static AppleSignedTransactionPayload DecodeSignedTransactionInfo(
        string signedTransactionInfo,
        X509Certificate2 trustedRoot = null)
    {
        return DecodeSignedPayload<AppleSignedTransactionPayload>(
            signedTransactionInfo,
            "Apple App Store did not return signed transaction info.",
            "Apple App Store returned malformed signed transaction info.",
            "Apple App Store returned an empty transaction payload.",
            "Failed to parse Apple App Store transaction data.",
            trustedRoot);
    }

    internal static AppleServerNotificationPayload DecodeServerNotificationPayload(
        string signedPayload,
        X509Certificate2 trustedRoot = null)
    {
        return DecodeSignedPayload<AppleServerNotificationPayload>(
            signedPayload,
            "Apple App Store did not return a signed notification payload.",
            "Apple App Store returned malformed signed notification payload.",
            "Apple App Store returned an empty notification payload.",
            "Failed to parse Apple App Store notification data.",
            trustedRoot);
    }

    internal static AppleSignedRenewalInfoPayload DecodeSignedRenewalInfo(
        string signedRenewalInfo,
        X509Certificate2 trustedRoot = null)
    {
        return DecodeSignedPayload<AppleSignedRenewalInfoPayload>(
            signedRenewalInfo,
            "Apple App Store did not return signed renewal info.",
            "Apple App Store returned malformed signed renewal info.",
            "Apple App Store returned an empty renewal payload.",
            "Failed to parse Apple App Store renewal data.",
            trustedRoot);
    }

    /// <summary>Apple subscription status codes from Get All Subscription Statuses.</summary>
    internal const int AppleStatusActive = 1;
    internal const int AppleStatusExpired = 2;
    internal const int AppleStatusBillingRetry = 3;
    internal const int AppleStatusGracePeriod = 4;
    internal const int AppleStatusRevoked = 5;

    internal static string DetermineSubscriptionStatus(
        DateTime utcNow,
        DateTime? expiryTimeUtc,
        DateTime? revocationTimeUtc,
        int? appleStatus = null)
    {
        if (revocationTimeUtc.HasValue || appleStatus == AppleStatusRevoked)
        {
            return SubscriptionStatuses.Cancelled;
        }

        // Grace period: the paid-through date has passed but Apple is retrying the renewal and the
        // customer is still entitled. Reading expiry alone would cut off a paying subscriber here.
        if (appleStatus == AppleStatusGracePeriod)
        {
            return SubscriptionStatuses.Active;
        }

        // Apple's own verdict outranks a date comparison when it says the subscription is over,
        // which covers an expiry Apple has moved without us seeing the renewal.
        if (appleStatus is AppleStatusExpired or AppleStatusBillingRetry)
        {
            return SubscriptionStatuses.Expired;
        }

        if (expiryTimeUtc.HasValue && expiryTimeUtc.Value <= utcNow)
        {
            return SubscriptionStatuses.Expired;
        }

        return SubscriptionStatuses.Active;
    }

    /// <summary>
    /// Picks the transaction that describes the subscription's current state. Apple returns the
    /// latest transaction per original transaction ID, so an entitled entry - if there is one - is
    /// the answer; otherwise the first entry still describes the lapsed subscription correctly.
    /// </summary>
    internal static AppleLastTransaction SelectCurrentTransaction(AppleSubscriptionStatusResponse response)
    {
        var transactions = response?.Data?
            .Where(group => group?.LastTransactions != null)
            .SelectMany(group => group.LastTransactions)
            .Where(transaction => transaction != null
                && !string.IsNullOrWhiteSpace(transaction.SignedTransactionInfo))
            .ToList();

        if (transactions == null || transactions.Count == 0)
        {
            throw new AppleAppStoreVerificationException(
                "Apple App Store returned no subscription transactions for this customer.");
        }

        return transactions.FirstOrDefault(transaction =>
                   transaction.Status is AppleStatusActive or AppleStatusGracePeriod)
               ?? transactions[0];
    }

    /// <summary>
    /// Resolves the recurring price, in currency units, from Apple's milliunit fields.
    /// </summary>
    /// <remarks>
    /// Apple omits price/currency from the signed transaction payload for subscription chains that
    /// predate those fields, and the controller only tolerates a missing price when a subscription
    /// record already exists — so a first-time subscriber on such a chain was rejected with a 503
    /// after Apple had already charged them. The renewal info in the same response still carries
    /// the recurring price, so it stands in when the transaction has none.
    /// </remarks>
    internal static (decimal? Price, string CurrencyCode) ResolveSubscriptionPrice(
        long? transactionPriceMilliunits,
        string transactionCurrency,
        long? renewalPriceMilliunits,
        string renewalCurrency)
    {
        if (transactionPriceMilliunits is > 0)
        {
            return (transactionPriceMilliunits.Value / 1000m, transactionCurrency);
        }

        if (renewalPriceMilliunits is > 0)
        {
            // A transaction that carries no price usually carries no currency either, but prefer
            // the transaction's own currency on the chance that only the price was missing.
            return (renewalPriceMilliunits.Value / 1000m,
                string.IsNullOrWhiteSpace(transactionCurrency) ? renewalCurrency : transactionCurrency);
        }

        return (null, transactionCurrency);
    }

    internal static string DetermineNotificationStatus(
        string notificationType,
        string subtype,
        DateTime utcNow,
        DateTime? expiryTimeUtc,
        DateTime? revocationTimeUtc,
        int? autoRenewStatus = null)
    {
        switch (notificationType)
        {
            case "DID_CHANGE_RENEWAL_STATUS":
                if (string.Equals(subtype, "AUTO_RENEW_DISABLED", StringComparison.OrdinalIgnoreCase) || autoRenewStatus == 0)
                {
                    return SubscriptionStatuses.Cancelled;
                }

                if (string.Equals(subtype, "AUTO_RENEW_ENABLED", StringComparison.OrdinalIgnoreCase) || autoRenewStatus == 1)
                {
                    return SubscriptionStatuses.Active;
                }

                break;
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
        string parseFailureMessage,
        X509Certificate2 trustedRoot)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(signedPayload))
        {
            throw new AppleAppStoreVerificationException(missingPayloadMessage);
        }

        var segments = signedPayload.Split('.');
        if (segments.Length != 3)
        {
            throw new AppleAppStoreVerificationException(malformedPayloadMessage);
        }

        try
        {
            VerifyAppleJwsSignature(segments, trustedRoot);
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

    private static void VerifyAppleJwsSignature(
        string[] segments,
        X509Certificate2 trustedRoot)
    {
        var headerJson = Base64UrlEncoder.Decode(segments[0]);
        var header = JsonSerializer.Deserialize<AppleJwsHeader>(
            headerJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (header == null
            || !string.Equals(header.Algorithm, SecurityAlgorithms.EcdsaSha256, StringComparison.Ordinal)
            || header.CertificateChain == null
            || header.CertificateChain.Length == 0)
        {
            throw new AppleAppStoreVerificationException(
                "Apple App Store signed data did not contain a valid ES256 certificate chain.");
        }

        var certificates = header.CertificateChain
            .Select(encoded => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded)))
            .ToArray();
        try
        {
            var effectiveTrustedRoot = trustedRoot ?? certificates[^1];
            var rootFingerprint = Convert.ToHexString(
                SHA256.HashData(effectiveTrustedRoot.RawData));
            if (trustedRoot == null && !AppleRootSha256Fingerprints.Contains(rootFingerprint))
            {
                throw new AppleAppStoreVerificationException(
                    "Apple App Store signed data did not chain to a pinned Apple root certificate.");
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            foreach (var intermediate in certificates.Skip(1).Where(certificate =>
                         !certificate.RawData.AsSpan().SequenceEqual(effectiveTrustedRoot.RawData)))
            {
                chain.ChainPolicy.ExtraStore.Add(intermediate);
            }

            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(effectiveTrustedRoot);

            if (!chain.Build(certificates[0]))
            {
                var errors = string.Join(
                    ", ",
                    chain.ChainStatus.Select(status => status.Status.ToString()));
                throw new AppleAppStoreVerificationException(
                    $"Apple App Store signed data certificate validation failed: {errors}.");
            }

            using var publicKey = certificates[0].GetECDsaPublicKey();
            if (publicKey == null)
            {
                throw new AppleAppStoreVerificationException(
                    "Apple App Store signed data did not use an EC signing certificate.");
            }

            var signedBytes = Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}");
            var signature = Base64UrlEncoder.DecodeBytes(segments[2]);
            if (!publicKey.VerifyData(
                    signedBytes,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new AppleAppStoreVerificationException(
                    "Apple App Store signed data signature validation failed.");
            }
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
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
        _sandboxApiBaseUrl = configuration["AppleAppStore:SandboxApiBaseUrl"] ?? "https://api.storekit-sandbox.itunes.apple.com";

        _privateKeyPath = ResolvePrivateKeyPath(configuration["AppleAppStore:PrivateKeyPath"], environment.ContentRootPath);
        var inlinePrivateKey = configuration["AppleAppStore:PrivateKeyPem"];
        _initializationError = DescribeCredentialConfigurationIssue(_privateKeyPath, inlinePrivateKey, _issuerId, _keyId, _bundleId);

        _logger.LogInformation(
            "Initializing Apple App Store verification service. BundleId={BundleId}, IssuerId={IssuerId}, KeyId={KeyId}, ApiBaseUrl={ApiBaseUrl}, SandboxApiBaseUrl={SandboxApiBaseUrl}, PrivateKeyPath={PrivateKeyPath}, PrivateKeyFileExists={PrivateKeyFileExists}, InlinePrivateKeyConfigured={InlinePrivateKeyConfigured}",
            _bundleId,
            _issuerId,
            _keyId,
            _apiBaseUrl,
            _sandboxApiBaseUrl,
            _privateKeyPath ?? "<none>",
            !string.IsNullOrWhiteSpace(_privateKeyPath) && File.Exists(_privateKeyPath),
            !string.IsNullOrWhiteSpace(inlinePrivateKey));

        try
        {
            var privateKeyPem = !string.IsNullOrWhiteSpace(_privateKeyPath) && File.Exists(_privateKeyPath)
                ? File.ReadAllText(_privateKeyPath)
                : inlinePrivateKey;

            if (!string.IsNullOrWhiteSpace(privateKeyPem))
            {
                _privateKey = LoadPrivateKey(privateKeyPem, out var loadStrategy);
                _logger.LogInformation(
                    "Apple App Store private key loaded successfully. PrivateKeySource={PrivateKeySource}, PemLength={PemLength}, LoadStrategy={LoadStrategy}",
                    !string.IsNullOrWhiteSpace(_privateKeyPath) && File.Exists(_privateKeyPath) ? _privateKeyPath : "inline-config",
                    privateKeyPem.Length,
                    loadStrategy);
            }
            else
            {
                _logger.LogWarning("Apple App Store verification key not available. {InitializationError}", _initializationError);
            }
        }
        catch (Exception ex)
        {
            _privateKey = null;
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
            // Get All Subscription Statuses, NOT Get Transaction Info.
            //
            // /inApps/v1/transactions/{id} answers "what did that one transaction contain", and for
            // an auto-renewable subscription every renewal is a separate transaction with its own
            // expiresDate. So any transaction but the very latest reports as expired, however
            // current the subscription actually is - which is exactly what happened: a subscription
            // bought minutes earlier verified as EXPIRED with an end date hours old, and successive
            // attempts reported different end dates as they walked different links in the chain.
            //
            // /inApps/v1/subscriptions/{id} accepts any transaction belonging to the subscription
            // and answers the question entitlement actually depends on: is it active right now.
            _logger.LogInformation(
                "Starting Apple App Store subscription verification. TransactionId={TransactionId}, ProductId={ProductId}, ApiBaseUrl={ApiBaseUrl}, BundleId={BundleId}, IssuerId={IssuerId}, KeyId={KeyId}, PrivateKeyPath={PrivateKeyPath}",
                transactionId,
                productId,
                _apiBaseUrl,
                _bundleId,
                _issuerId,
                _keyId,
                _privateKeyPath ?? "<none>");

            var response = await SendSubscriptionStatusRequestAsync(_apiBaseUrl, transactionId);

            // A transaction exists only in the environment it was bought in, and Apple keeps the two
            // in separate namespaces. TestFlight builds and App Review both purchase in *sandbox*
            // while talking to the production server, so production answers 404 for a transaction
            // that is perfectly valid - which made every sandbox purchase fail verification.
            //
            // Apple's guidance is to ask production first and fall back to sandbox on 404. The order
            // matters and must never be reversed: asking sandbox first would let a sandbox purchase
            // satisfy a request that production could have answered for real.
            if (response.StatusCode == HttpStatusCode.NotFound && ShouldRetryAgainstSandbox())
            {
                response.Dispose();
                _logger.LogInformation(
                    "Apple App Store did not recognise transaction {TransactionId} at {ApiBaseUrl}. Retrying against the sandbox environment at {SandboxApiBaseUrl}.",
                    transactionId,
                    _apiBaseUrl,
                    _sandboxApiBaseUrl);
                response = await SendSubscriptionStatusRequestAsync(_sandboxApiBaseUrl, transactionId);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AppleAppStoreVerificationException("Apple App Store could not find this transaction for the configured app.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Apple App Store API access denied with status {StatusCode}. Body: {Body}",
                    response.StatusCode,
                    errorBody);
                throw new AppleAppStoreVerificationException(DescribeApiAccessDenied(response.StatusCode, errorBody));
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

            var statusResponse = await response.Content.ReadFromJsonAsync<AppleSubscriptionStatusResponse>();
            var latestTransaction = SelectCurrentTransaction(statusResponse);
            _logger.LogInformation(
                "Apple App Store verification response payload received. GroupCount={GroupCount}, AppleStatus={AppleStatus}",
                statusResponse?.Data?.Count ?? 0,
                latestTransaction.Status);
            var payload = DecodeSignedTransactionInfo(latestTransaction.SignedTransactionInfo);
            _logger.LogInformation(
                "Decoded Apple signed transaction payload. PayloadTransactionId={PayloadTransactionId}, PayloadOriginalTransactionId={PayloadOriginalTransactionId}, PayloadProductId={PayloadProductId}, PayloadBundleId={PayloadBundleId}, PayloadEnvironment={PayloadEnvironment}",
                payload.TransactionId,
                payload.OriginalTransactionId,
                payload.ProductId,
                payload.BundleId,
                payload.Environment);
            ValidatePayload(payload, transactionId, productId);

            var expiryTime = payload.ExpiresDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(payload.ExpiresDate.Value).UtcDateTime
                : (DateTime?)null;
            var purchaseTime = payload.PurchaseDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(payload.PurchaseDate.Value).UtcDateTime
                : (DateTime?)null;
            var revocationTime = payload.RevocationDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(payload.RevocationDate.Value).UtcDateTime
                : (DateTime?)null;
            // Only worth decoding the renewal info when the transaction itself came without a price.
            AppleSignedRenewalInfoPayload renewalInfo = null;
            if (payload.Price is not > 0 && !string.IsNullOrWhiteSpace(latestTransaction.SignedRenewalInfo))
            {
                try
                {
                    renewalInfo = DecodeSignedRenewalInfo(latestTransaction.SignedRenewalInfo);
                }
                catch (AppleAppStoreVerificationException ex)
                {
                    // A malformed renewal payload must not sink an otherwise-valid verification;
                    // leaving the price unresolved just restores the previous behaviour.
                    _logger.LogWarning(
                        ex,
                        "Could not read renewal info while looking for a fallback price for transaction {TransactionId}",
                        payload.TransactionId);
                }
            }

            var (transactionPrice, priceCurrencyCode) = ResolveSubscriptionPrice(
                payload.Price,
                payload.Currency,
                renewalInfo?.RenewalPrice,
                renewalInfo?.Currency);

            if (transactionPrice.HasValue && payload.Price is not > 0)
            {
                _logger.LogInformation(
                    "Apple transaction {TransactionId} carried no price; falling back to the renewal price {RenewalPrice} {PriceCurrencyCode}",
                    payload.TransactionId,
                    transactionPrice,
                    priceCurrencyCode ?? "<none>");
            }

            var resolvedStatus = DetermineSubscriptionStatus(
                DateTime.UtcNow, expiryTime, revocationTime, latestTransaction.Status);
            _logger.LogInformation(
                "Apple subscription verification succeeded. TransactionId={TransactionId}, OriginalTransactionId={OriginalTransactionId}, ProductId={ProductId}, Environment={Environment}, ExpiryTimeUtc={ExpiryTimeUtc}, RevocationTimeUtc={RevocationTimeUtc}, ResolvedStatus={ResolvedStatus}",
                payload.TransactionId,
                payload.OriginalTransactionId,
                payload.ProductId,
                payload.Environment,
                expiryTime,
                revocationTime,
                resolvedStatus);

            return new AppleAppStoreSubscriptionInfo(
                resolvedStatus,
                expiryTime,
                purchaseTime,
                payload.TransactionId,
                payload.OriginalTransactionId,
                payload.ProductId,
                payload.Environment,
                payload.AppAccountToken,
                transactionPrice,
                priceCurrencyCode);
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

    /// <summary>
    /// Whether a 404 from the configured environment is worth a second look in sandbox. Pointless
    /// when the server is already pointed at sandbox - the answer would be the same 404 - and
    /// impossible when no sandbox URL is configured.
    /// </summary>
    internal static bool ShouldRetryAgainstSandbox(string apiBaseUrl, string sandboxApiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(sandboxApiBaseUrl))
        {
            return false;
        }

        return !string.Equals(
            apiBaseUrl?.TrimEnd('/'),
            sandboxApiBaseUrl.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldRetryAgainstSandbox()
        => ShouldRetryAgainstSandbox(_apiBaseUrl, _sandboxApiBaseUrl);

    /// <summary>
    /// Asks one Apple environment about a subscription. Split out so the same request can be replayed
    /// against sandbox without duplicating the token and logging setup.
    /// </summary>
    private async Task<HttpResponseMessage> SendSubscriptionStatusRequestAsync(string apiBaseUrl, string transactionId)
    {
        var requestUri = $"{apiBaseUrl.TrimEnd('/')}/inApps/v1/subscriptions/{Uri.EscapeDataString(transactionId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        var bearerToken = CreateBearerToken();
        _logger.LogInformation("Created Apple App Store bearer token summary: {BearerTokenSummary}", DescribeBearerToken(bearerToken));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var client = _httpClientFactory.CreateClient();
        _logger.LogInformation("Sending Apple App Store verification request to {RequestUri}", requestUri);
        var response = await client.SendAsync(request);
        _logger.LogInformation(
            "Apple App Store verification response received. RequestUri={RequestUri}, StatusCode={StatusCode}",
            requestUri,
            response.StatusCode);

        return response;
    }

    private string CreateBearerToken()
    {
        var tokenDescriptor = CreateBearerTokenDescriptor(_issuerId, _bundleId, _keyId, _privateKey);

        return new JsonWebTokenHandler().CreateToken(tokenDescriptor);
    }

    internal static SecurityTokenDescriptor CreateBearerTokenDescriptor(string issuerId, string bundleId, string keyId, ECDsa privateKey)
    {
        return new SecurityTokenDescriptor
        {
            Issuer = issuerId,
            Audience = "appstoreconnect-v1",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["bid"] = bundleId
            },
            Subject = new ClaimsIdentity(),
            SigningCredentials = new SigningCredentials(
                new ECDsaSecurityKey(privateKey) { KeyId = keyId },
                SecurityAlgorithms.EcdsaSha256)
        };
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

        // Deliberately NOT an equality check against the requested transaction ID any more. The
        // subscription-statuses endpoint answers with the subscription's *latest* transaction, which
        // for anything that has renewed is by definition not the one the client had on hand — so
        // requiring equality rejected exactly the renewed subscriptions this call exists to confirm.
        //
        // Nothing security-relevant is lost. The bundle and product checks above already pin the
        // response to this app's subscription, the bearer token is scoped to our bundle, and
        // ownership is enforced separately in the controller, which refuses to attach a transaction
        // chain that already belongs to another user.
        if (!string.Equals(payload.TransactionId, transactionId, StringComparison.Ordinal) &&
            !string.Equals(payload.OriginalTransactionId, transactionId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Apple resolved requested transaction {RequestedTransactionId} to a later transaction in the same subscription. ResolvedTransactionId={ResolvedTransactionId}, OriginalTransactionId={OriginalTransactionId}",
                transactionId,
                payload.TransactionId,
                payload.OriginalTransactionId);
        }
    }

    internal sealed class AppleTransactionLookupResponse
    {
        public string SignedTransactionInfo { get; set; }
    }

    /// <summary>
    /// Shape of Apple's Get All Subscription Statuses response. Each entry in <see cref="Data"/> is
    /// one subscription group, and its <c>lastTransactions</c> carry the most recent transaction per
    /// original transaction ID — which is what makes this the right call for "is it active now".
    /// </summary>
    internal sealed class AppleSubscriptionStatusResponse
    {
        public string Environment { get; set; }
        public string BundleId { get; set; }
        public List<AppleSubscriptionGroup> Data { get; set; }
    }

    internal sealed class AppleSubscriptionGroup
    {
        public string SubscriptionGroupIdentifier { get; set; }
        public List<AppleLastTransaction> LastTransactions { get; set; }
    }

    internal sealed class AppleLastTransaction
    {
        public string OriginalTransactionId { get; set; }

        /// <summary>Apple's status code: 1 active, 2 expired, 3 billing retry, 4 grace period, 5 revoked.</summary>
        public int? Status { get; set; }

        public string SignedTransactionInfo { get; set; }
        public string SignedRenewalInfo { get; set; }
    }

    internal sealed class AppleSignedTransactionPayload
    {
        public string TransactionId { get; set; }
        public string OriginalTransactionId { get; set; }
        public string ProductId { get; set; }
        public string BundleId { get; set; }
        public string Environment { get; set; }
        public string AppAccountToken { get; set; }
        public long? PurchaseDate { get; set; }
        public long? ExpiresDate { get; set; }
        public long? RevocationDate { get; set; }
        public long? Price { get; set; }
        public string Currency { get; set; }
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

    internal sealed class AppleSignedRenewalInfoPayload
    {
        public int? AutoRenewStatus { get; set; }
        public long? RenewalPrice { get; set; }
        public string Currency { get; set; }
    }

    public AppleAppStoreServerNotificationInfo VerifyServerNotification(string signedPayload)
    {
        var notification = DecodeServerNotificationPayload(signedPayload);
        var transaction = DecodeSignedTransactionInfo(notification.Data?.SignedTransactionInfo);
        var renewal = !string.IsNullOrWhiteSpace(notification.Data?.SignedRenewalInfo)
            ? DecodeSignedRenewalInfo(notification.Data.SignedRenewalInfo)
            : null;

        if (!string.Equals(transaction.BundleId, _bundleId, StringComparison.Ordinal))
        {
            throw new AppleAppStoreVerificationException(
                "Apple App Store notification bundle ID does not match the configured app.");
        }

        return new AppleAppStoreServerNotificationInfo(
            notification.NotificationType,
            notification.Subtype,
            new AppleAppStoreServerTransactionInfo(
                transaction.TransactionId,
                transaction.OriginalTransactionId,
                transaction.ProductId,
                transaction.BundleId,
                transaction.Environment,
                transaction.AppAccountToken,
                transaction.ExpiresDate,
                transaction.RevocationDate,
                transaction.Price,
                transaction.Currency),
            renewal == null
                ? null
                : new AppleAppStoreServerRenewalInfo(
                    renewal.AutoRenewStatus,
                    renewal.RenewalPrice,
                    renewal.Currency));
    }

    private sealed class AppleJwsHeader
    {
        [JsonPropertyName("alg")]
        public string Algorithm { get; set; }

        [JsonPropertyName("x5c")]
        public string[] CertificateChain { get; set; }
    }
}
