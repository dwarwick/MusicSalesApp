using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moq;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AppleIdentityTokenValidatorTests
{
    private const string BundleId = "net.streamtunes.musicsalesapp.maui";
    private const string KeyId = "test-apple-key";

    private RSA _rsa;
    private RsaSecurityKey _signingKey;
    private AppleIdentityTokenValidator _validator;

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };

        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(_signingKey);

        _validator = new AppleIdentityTokenValidator(
            BundleId,
            new StubConfigurationManager(configuration),
            Mock.Of<ILogger<AppleIdentityTokenValidator>>());
    }

    [TearDown]
    public void TearDown() => _rsa.Dispose();

    private string CreateToken(
        string subject = "001234.abcdef.0000",
        string issuer = AppleIdentityTokenValidator.AppleIssuer,
        string audience = BundleId,
        IEnumerable<Claim> extraClaims = null,
        DateTime? expires = null)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (extraClaims != null)
        {
            claims.AddRange(extraClaims);
        }

        // NotBefore is anchored to the expiry rather than to "now" so an intentionally expired
        // token is still internally consistent and reaches the validator.
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer,
            Audience = audience,
            NotBefore = expiry.AddMinutes(-30),
            IssuedAt = expiry.AddMinutes(-30),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    [Test]
    public async Task ValidateAsync_ValidToken_ReturnsSubjectAndEmail()
    {
        var token = CreateToken(extraClaims:
        [
            new Claim("email", "someone@privaterelay.appleid.com"),
            new Claim("email_verified", "true"),
            new Claim("is_private_email", "true")
        ]);

        var (success, payload, error) = await _validator.ValidateAsync(token);

        Assert.That(success, Is.True, error);
        Assert.That(payload.Subject, Is.EqualTo("001234.abcdef.0000"));
        Assert.That(payload.Email, Is.EqualTo("someone@privaterelay.appleid.com"));
        Assert.That(payload.EmailVerified, Is.True);
        Assert.That(payload.IsPrivateEmail, Is.True);
    }

    [Test]
    public async Task ValidateAsync_ReturningUserTokenWithNoEmail_StillSucceeds()
    {
        // Every sign-in after the first carries only the subject. That is not an error.
        var (success, payload, _) = await _validator.ValidateAsync(CreateToken());

        Assert.That(success, Is.True);
        Assert.That(payload.Subject, Is.EqualTo("001234.abcdef.0000"));
        Assert.That(payload.Email, Is.Empty);
    }

    [Test]
    public async Task ValidateAsync_WrongAudience_Fails()
    {
        var token = CreateToken(audience: "com.someone.else");

        var (success, payload, error) = await _validator.ValidateAsync(token);

        Assert.That(success, Is.False);
        Assert.That(payload, Is.Null);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public async Task ValidateAsync_WrongIssuer_Fails()
    {
        var token = CreateToken(issuer: "https://accounts.google.com");

        var (success, _, _) = await _validator.ValidateAsync(token);

        Assert.That(success, Is.False);
    }

    [Test]
    public async Task ValidateAsync_ExpiredToken_Fails()
    {
        var token = CreateToken(expires: DateTime.UtcNow.AddHours(-2));

        var (success, _, error) = await _validator.ValidateAsync(token);

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("expired"));
    }

    [Test]
    public async Task ValidateAsync_TokenSignedByAnotherKey_Fails()
    {
        using var otherRsa = RSA.Create(2048);
        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", "001234.abcdef.0000")]),
            Issuer = AppleIdentityTokenValidator.AppleIssuer,
            Audience = BundleId,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(otherRsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256)
        }));

        var (success, _, _) = await _validator.ValidateAsync(token);

        Assert.That(success, Is.False);
    }

    [Test]
    public async Task ValidateAsync_WhenNotConfigured_FailsWithoutCallingApple()
    {
        var validator = new AppleIdentityTokenValidator(
            string.Empty,
            new StubConfigurationManager(new OpenIdConnectConfiguration()),
            Mock.Of<ILogger<AppleIdentityTokenValidator>>());

        Assert.That(validator.IsConfigured, Is.False);

        var (success, _, error) = await validator.ValidateAsync("anything");

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("not configured"));
    }

    [Test]
    public void IsConfigured_FallsBackToAppleAppStoreBundleId()
    {
        // Every environment already carries AppleAppStore:BundleId for receipt verification, and
        // it is the same bundle id the identity token is audienced to.
        var configuration = new Mock<IConfiguration>();
        configuration.Setup(c => c["Authentication:Apple:BundleId"]).Returns((string)null);
        configuration.Setup(c => c["AppleAppStore:BundleId"]).Returns(BundleId);

        var validator = new AppleIdentityTokenValidator(
            configuration.Object,
            Mock.Of<ILogger<AppleIdentityTokenValidator>>());

        Assert.That(validator.IsConfigured, Is.True);
    }

    private sealed class StubConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly OpenIdConnectConfiguration _configuration;

        public StubConfigurationManager(OpenIdConnectConfiguration configuration) => _configuration = configuration;

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
            => Task.FromResult(_configuration);

        public void RequestRefresh()
        {
        }
    }
}
