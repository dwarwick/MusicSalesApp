using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AppleAppStoreVerificationServiceTests
{
    [Test]
    public void ResolvePrivateKeyPath_ReturnsNull_WhenPathMissing()
    {
        var result = AppleAppStoreVerificationService.ResolvePrivateKeyPath(null, @"C:\app-root");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void DescribeCredentialConfigurationIssue_ReturnsMissingKeyMessage_WhenKeyMissing()
    {
        var result = AppleAppStoreVerificationService.DescribeCredentialConfigurationIssue(
            null,
            null,
            "issuer-id",
            "key-id",
            "net.streamtunes.musicsalesapp.maui");

        Assert.That(result, Is.EqualTo("Apple App Store private key is not configured on the server."));
    }

    [Test]
    public void DecodeSignedTransactionInfo_ParsesPayload()
    {
        using var signed = CreateSignedPayload("{\"transactionId\":\"tx-123\",\"originalTransactionId\":\"orig-123\",\"productId\":\"streamtunes_monthly_sub_ios\",\"bundleId\":\"net.streamtunes.musicsalesapp.maui\",\"environment\":\"Sandbox\",\"appAccountToken\":\"acct-123\",\"expiresDate\":1893456000000,\"price\":990,\"currency\":\"USD\"}");

        var result = AppleAppStoreVerificationService.DecodeSignedTransactionInfo(
            signed.Value,
            signed.TrustedRoot);

        Assert.Multiple(() =>
        {
            Assert.That(result.TransactionId, Is.EqualTo("tx-123"));
            Assert.That(result.OriginalTransactionId, Is.EqualTo("orig-123"));
            Assert.That(result.ProductId, Is.EqualTo("streamtunes_monthly_sub_ios"));
            Assert.That(result.BundleId, Is.EqualTo("net.streamtunes.musicsalesapp.maui"));
            Assert.That(result.Environment, Is.EqualTo("Sandbox"));
            Assert.That(result.AppAccountToken, Is.EqualTo("acct-123"));
            Assert.That(result.ExpiresDate, Is.EqualTo(1893456000000));
            Assert.That(result.Price, Is.EqualTo(990));
            Assert.That(result.Currency, Is.EqualTo("USD"));
        });
    }

    [Test]
    public void DecodeServerNotificationPayload_ParsesPayload()
    {
        using var signed = CreateSignedPayload("{\"notificationType\":\"DID_RENEW\",\"subtype\":\"INITIAL_BUY\",\"data\":{\"signedTransactionInfo\":\"header.inner.signature\"}}");

        var result = AppleAppStoreVerificationService.DecodeServerNotificationPayload(
            signed.Value,
            signed.TrustedRoot);

        Assert.Multiple(() =>
        {
            Assert.That(result.NotificationType, Is.EqualTo("DID_RENEW"));
            Assert.That(result.Subtype, Is.EqualTo("INITIAL_BUY"));
            Assert.That(result.Data, Is.Not.Null);
            Assert.That(result.Data.SignedTransactionInfo, Is.EqualTo("header.inner.signature"));
        });
    }

    [Test]
    public void DecodeSignedRenewalInfo_ParsesPayload()
    {
        using var signed = CreateSignedPayload("{\"autoRenewStatus\":0,\"renewalPrice\":990,\"currency\":\"USD\"}");

        var result = AppleAppStoreVerificationService.DecodeSignedRenewalInfo(
            signed.Value,
            signed.TrustedRoot);

        Assert.Multiple(() =>
        {
            Assert.That(result.AutoRenewStatus, Is.EqualTo(0));
            Assert.That(result.RenewalPrice, Is.EqualTo(990));
            Assert.That(result.Currency, Is.EqualTo("USD"));
        });
    }

    [Test]
    public void DecodeSignedTransactionInfo_RejectsTamperedPayload()
    {
        using var signed = CreateSignedPayload("{\"transactionId\":\"tx-original\"}");
        var segments = signed.Value.Split('.');
        segments[1] = Base64UrlEncoder.Encode("{\"transactionId\":\"tx-forged\"}");

        var exception = Assert.Throws<AppleAppStoreVerificationException>(() =>
            AppleAppStoreVerificationService.DecodeSignedTransactionInfo(
                string.Join('.', segments),
                signed.TrustedRoot));

        Assert.That(exception!.Message, Does.Contain("signature validation failed"));
    }

    [Test]
    public void DetermineSubscriptionStatus_ReturnsCancelled_WhenRevoked()
    {
        var result = AppleAppStoreVerificationService.DetermineSubscriptionStatus(
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(10),
            DateTime.UtcNow.AddMinutes(-1));

        Assert.That(result, Is.EqualTo(SubscriptionStatuses.Cancelled));
    }

    [Test]
    public void DetermineNotificationStatus_ReturnsSuspended_ForFailedRenewal()
    {
        var result = AppleAppStoreVerificationService.DetermineNotificationStatus(
            "DID_FAIL_TO_RENEW",
            null,
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(5),
            null);

        Assert.That(result, Is.EqualTo(SubscriptionStatuses.Suspended));
    }

    [Test]
    public void DetermineNotificationStatus_ReturnsCancelled_ForAutoRenewDisabled()
    {
        var result = AppleAppStoreVerificationService.DetermineNotificationStatus(
            "DID_CHANGE_RENEWAL_STATUS",
            "AUTO_RENEW_DISABLED",
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(5),
            null,
            0);

        Assert.That(result, Is.EqualTo(SubscriptionStatuses.Cancelled));
    }

    [Test]
    public void CreateBearerTokenDescriptor_IncludesBundleIdClaim()
    {
        using var privateKey = ECDsa.Create();

        var descriptor = AppleAppStoreVerificationService.CreateBearerTokenDescriptor(
            "issuer-id",
            "net.streamtunes.musicsalesapp.maui",
            "key-id",
            privateKey);

        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Issuer, Is.EqualTo("issuer-id"));
            Assert.That(descriptor.Audience, Is.EqualTo("appstoreconnect-v1"));
            Assert.That(descriptor.Claims, Contains.Key("bid"));
            Assert.That(descriptor.Claims!["bid"], Is.EqualTo("net.streamtunes.musicsalesapp.maui"));
        });
    }

    [Test]
    public void DescribeBearerToken_ReturnsExpectedSummary()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = AppleAppStoreVerificationService.CreateBearerTokenDescriptor(
            "issuer-id",
            "net.streamtunes.musicsalesapp.maui",
            "key-id",
            privateKey);
        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        var result = AppleAppStoreVerificationService.DescribeBearerToken(token);

        Assert.That(result, Does.Contain("kid=key-id"));
        Assert.That(result, Does.Contain("iss=issuer-id"));
        Assert.That(result, Does.Contain("aud=appstoreconnect-v1"));
        Assert.That(result, Does.Contain("bid=net.streamtunes.musicsalesapp.maui"));
    }

    [Test]
    public void DescribeApiAccessDenied_UsesAppleErrorPayload_WhenPresent()
    {
        var result = AppleAppStoreVerificationService.DescribeApiAccessDenied(
            HttpStatusCode.Unauthorized,
            "{\"errorCode\":4010002,\"errorMessage\":\"Invalid issuer\"}");

        Assert.That(result, Is.EqualTo("Apple App Store API access was denied. Check the issuer ID, key ID, private key, and App Store Connect permissions. Apple response (401): 4010002 - Invalid issuer"));
    }

    [Test]
    public async Task VerifySubscriptionAsync_IncludesAppleErrorDetails_WhenAccessDenied()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPem = privateKey.ExportPkcs8PrivateKeyPem();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["AppleAppStore:BundleId"] = "net.streamtunes.musicsalesapp.maui",
                ["AppleAppStore:IssuerId"] = "issuer-id",
                ["AppleAppStore:KeyId"] = "key-id",
                ["AppleAppStore:PrivateKeyPem"] = privateKeyPem,
                ["AppleAppStore:ApiBaseUrl"] = "https://api.storekit-sandbox.itunes.apple.com"
            })
            .Build();

        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"errorCode\":4010002,\"errorMessage\":\"Invalid issuer\"}")
            });

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler.Object));

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(value => value.ContentRootPath).Returns("/tmp");

        var service = new AppleAppStoreVerificationService(
            configuration,
            environment.Object,
            httpClientFactory.Object,
            NullLogger<AppleAppStoreVerificationService>.Instance);

        var exception = Assert.ThrowsAsync<AppleAppStoreVerificationException>(() => service.VerifySubscriptionAsync("tx-123", "streamtunes_monthly_sub_ios"));

        Assert.That(exception!.Message, Does.Contain("4010002 - Invalid issuer"));
    }

    [Test]
    public void VerifySubscriptionAsync_DoesNotProceed_WhenPrivateKeyCannotBeLoaded()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["AppleAppStore:BundleId"] = "net.streamtunes.musicsalesapp.maui",
                ["AppleAppStore:IssuerId"] = "issuer-id",
                ["AppleAppStore:KeyId"] = "key-id",
                ["AppleAppStore:PrivateKeyPem"] = "not-a-valid-private-key",
                ["AppleAppStore:ApiBaseUrl"] = "https://api.storekit-sandbox.itunes.apple.com"
            })
            .Build();

        var httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(value => value.ContentRootPath).Returns("/tmp");

        var service = new AppleAppStoreVerificationService(
            configuration,
            environment.Object,
            httpClientFactory.Object,
            NullLogger<AppleAppStoreVerificationService>.Instance);

        var exception = Assert.ThrowsAsync<AppleAppStoreVerificationException>(() => service.VerifySubscriptionAsync("tx-123", "streamtunes_monthly_sub_ios"));

        Assert.That(exception!.Message, Is.EqualTo("Apple App Store private key could not be loaded on the server."));
        httpClientFactory.Verify(factory => factory.CreateClient(It.IsAny<string>()), Times.Never);
    }

    private static SignedPayload CreateSignedPayload(string payloadJson)
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest(
            "CN=Test Apple Root",
            rootKey,
            HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var root = rootRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest(
            "CN=Test Apple Signing",
            leafKey,
            HashAlgorithmName.SHA256);
        leafRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var serialNumber = RandomNumberGenerator.GetBytes(16);
        using var issuedLeaf = leafRequest.Create(
            root,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(7),
            serialNumber);
        var leaf = issuedLeaf.CopyWithPrivateKey(leafKey);

        var headerJson = JsonSerializer.Serialize(new
        {
            alg = "ES256",
            x5c = new[]
            {
                Convert.ToBase64String(leaf.RawData),
                Convert.ToBase64String(root.RawData)
            }
        });
        var header = Base64UrlEncoder.Encode(headerJson);
        var payload = Base64UrlEncoder.Encode(payloadJson);
        var signedBytes = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var signature = leafKey.SignData(
            signedBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new SignedPayload(
            $"{header}.{payload}.{Base64UrlEncoder.Encode(signature)}",
            root,
            leaf);
    }

    private sealed class SignedPayload(
        string value,
        X509Certificate2 trustedRoot,
        X509Certificate2 leaf) : IDisposable
    {
        public string Value { get; } = value;
        public X509Certificate2 TrustedRoot { get; } = trustedRoot;

        public void Dispose()
        {
            leaf.Dispose();
            TrustedRoot.Dispose();
        }
    }
}
