using Microsoft.IdentityModel.Tokens;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

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
}