using System.Security.Cryptography;
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
        var payload = Base64UrlEncoder.Encode("{\"transactionId\":\"tx-123\",\"originalTransactionId\":\"orig-123\",\"productId\":\"streamtunes_monthly_sub_ios\",\"bundleId\":\"net.streamtunes.musicsalesapp.maui\",\"environment\":\"Sandbox\",\"appAccountToken\":\"acct-123\",\"expiresDate\":1893456000000}");
        var signedTransactionInfo = $"header.{payload}.signature";

        var result = AppleAppStoreVerificationService.DecodeSignedTransactionInfo(signedTransactionInfo);

        Assert.Multiple(() =>
        {
            Assert.That(result.TransactionId, Is.EqualTo("tx-123"));
            Assert.That(result.OriginalTransactionId, Is.EqualTo("orig-123"));
            Assert.That(result.ProductId, Is.EqualTo("streamtunes_monthly_sub_ios"));
            Assert.That(result.BundleId, Is.EqualTo("net.streamtunes.musicsalesapp.maui"));
            Assert.That(result.Environment, Is.EqualTo("Sandbox"));
            Assert.That(result.AppAccountToken, Is.EqualTo("acct-123"));
            Assert.That(result.ExpiresDate, Is.EqualTo(1893456000000));
        });
    }

    [Test]
    public void DecodeServerNotificationPayload_ParsesPayload()
    {
        var payload = Base64UrlEncoder.Encode("{\"notificationType\":\"DID_RENEW\",\"subtype\":\"INITIAL_BUY\",\"data\":{\"signedTransactionInfo\":\"header.inner.signature\"}}");
        var signedPayload = $"header.{payload}.signature";

        var result = AppleAppStoreVerificationService.DecodeServerNotificationPayload(signedPayload);

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
        var payload = Base64UrlEncoder.Encode("{\"autoRenewStatus\":0}");
        var signedPayload = $"header.{payload}.signature";

        var result = AppleAppStoreVerificationService.DecodeSignedRenewalInfo(signedPayload);

        Assert.That(result.AutoRenewStatus, Is.EqualTo(0));
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
}