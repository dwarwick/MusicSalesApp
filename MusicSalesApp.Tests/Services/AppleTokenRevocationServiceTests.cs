using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AppleTokenRevocationServiceTests
{
    private const string TeamId = "K7ZGP97YV6";
    private const string KeyId = "TESTKEY123";
    private const string BundleId = "net.streamtunes.musicsalesapp.maui";

    private string _keyPath;

    [SetUp]
    public void SetUp()
    {
        // A throwaway P-256 key in the same PEM shape Apple issues.
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        _keyPath = Path.Combine(Path.GetTempPath(), $"apple-signin-{Guid.NewGuid():N}.p8");
        File.WriteAllText(_keyPath, ecdsa.ExportPkcs8PrivateKeyPem());
    }

    [TearDown]
    public void TearDown()
    {
        if (_keyPath is not null && File.Exists(_keyPath))
        {
            File.Delete(_keyPath);
        }
    }

    private AppleTokenRevocationService CreateService(
        string teamId = TeamId,
        string keyId = KeyId,
        string bundleId = BundleId,
        string privateKeyPath = null,
        bool usePath = true)
    {
        var configuration = new Mock<IConfiguration>();
        configuration.Setup(c => c["Authentication:Apple:TeamId"]).Returns(teamId);
        configuration.Setup(c => c["Authentication:Apple:KeyId"]).Returns(keyId);
        configuration.Setup(c => c["Authentication:Apple:BundleId"]).Returns(bundleId);
        configuration.Setup(c => c["AppleAppStore:BundleId"]).Returns((string)null);
        configuration.Setup(c => c["Authentication:Apple:PrivateKeyPem"]).Returns((string)null);
        configuration.Setup(c => c["Authentication:Apple:PrivateKeyPath"])
            .Returns(usePath ? privateKeyPath ?? _keyPath : null);

        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());

        return new AppleTokenRevocationService(
            configuration.Object,
            environment.Object,
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ILogger<AppleTokenRevocationService>>());
    }

    [Test]
    public void IsConfigured_WithKeyTeamAndClientId_IsTrue()
    {
        using var service = CreateService();
        Assert.That(service.IsConfigured, Is.True);
    }

    [Test]
    public void IsConfigured_WithoutKeyFile_IsFalse()
    {
        using var service = CreateService(usePath: false);
        Assert.That(service.IsConfigured, Is.False);
    }

    [Test]
    public void IsConfigured_WithoutTeamId_IsFalse()
    {
        using var service = CreateService(teamId: null);
        Assert.That(service.IsConfigured, Is.False);
    }

    [Test]
    public void IsConfigured_WithoutKeyId_IsFalse()
    {
        using var service = CreateService(keyId: "");
        Assert.That(service.IsConfigured, Is.False);
    }

    [Test]
    public void IsConfigured_FallsBackToAppleAppStoreBundleId()
    {
        var configuration = new Mock<IConfiguration>();
        configuration.Setup(c => c["Authentication:Apple:TeamId"]).Returns(TeamId);
        configuration.Setup(c => c["Authentication:Apple:KeyId"]).Returns(KeyId);
        configuration.Setup(c => c["Authentication:Apple:BundleId"]).Returns((string)null);
        configuration.Setup(c => c["AppleAppStore:BundleId"]).Returns(BundleId);
        configuration.Setup(c => c["Authentication:Apple:PrivateKeyPem"]).Returns((string)null);
        configuration.Setup(c => c["Authentication:Apple:PrivateKeyPath"]).Returns(_keyPath);

        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());

        using var service = new AppleTokenRevocationService(
            configuration.Object, environment.Object,
            Mock.Of<IHttpClientFactory>(), Mock.Of<ILogger<AppleTokenRevocationService>>());

        Assert.That(service.IsConfigured, Is.True);
    }

    [Test]
    public async Task WhenUnconfigured_BothCallsShortCircuitWithoutTouchingTheNetwork()
    {
        // The HttpClientFactory is a bare mock: any real call would throw, so reaching the network
        // here would fail the test rather than pass silently.
        using var service = CreateService(usePath: false);

        Assert.That(await service.ExchangeAuthorizationCodeAsync("code"), Is.Null);
        Assert.That(await service.RevokeRefreshTokenAsync("token"), Is.False);
    }

    [Test]
    public async Task WhenConfigured_BlankInputsStillShortCircuit()
    {
        using var service = CreateService();

        Assert.That(await service.ExchangeAuthorizationCodeAsync(""), Is.Null);
        Assert.That(await service.RevokeRefreshTokenAsync(null), Is.False);
    }
    [Test]
    public void IsConfigured_WithWindowsStyleRelativePath_ResolvesOnAnyPlatform()
    {
        // The three appsettings.{Env}.json files spell this path the Windows way to match the
        // sibling AppleAppStore key, because production is IIS. It still has to resolve when the
        // API is run on macOS or Linux.
        var contentRoot = Path.Combine(Path.GetTempPath(), $"apple-root-{Guid.NewGuid():N}");
        var secretsDirectory = Path.Combine(contentRoot, "App_Data", "Secrets");
        Directory.CreateDirectory(secretsDirectory);
        File.Copy(_keyPath, Path.Combine(secretsDirectory, "AuthKey_TEST.p8"));

        try
        {
            var configuration = new Mock<IConfiguration>();
            configuration.Setup(c => c["Authentication:Apple:TeamId"]).Returns(TeamId);
            configuration.Setup(c => c["Authentication:Apple:KeyId"]).Returns(KeyId);
            configuration.Setup(c => c["Authentication:Apple:BundleId"]).Returns(BundleId);
            configuration.Setup(c => c["AppleAppStore:BundleId"]).Returns((string)null);
            configuration.Setup(c => c["Authentication:Apple:PrivateKeyPem"]).Returns((string)null);
            configuration.Setup(c => c["Authentication:Apple:PrivateKeyPath"])
                .Returns(@"App_Data\Secrets\AuthKey_TEST.p8");

            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(e => e.ContentRootPath).Returns(contentRoot);

            using var service = new AppleTokenRevocationService(
                configuration.Object, environment.Object,
                Mock.Of<IHttpClientFactory>(), Mock.Of<ILogger<AppleTokenRevocationService>>());

            Assert.That(service.IsConfigured, Is.True);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
