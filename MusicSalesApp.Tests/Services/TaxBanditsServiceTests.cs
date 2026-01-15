using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Protected;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class TaxBanditsServiceTests
{
    private Mock<HttpMessageHandler> _mockHttpMessageHandler;
    private HttpClient _httpClient;
    private Mock<Microsoft.Extensions.Logging.ILogger<TaxBanditsService>> _mockLogger;
    private Mock<IConfiguration> _mockConfiguration;
    private TaxBanditsService _service;

    [SetUp]
    public void SetUp()
    {
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpMessageHandler.Object);
        _mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<TaxBanditsService>>();
        _mockConfiguration = new Mock<IConfiguration>();
        
        // Setup default configuration values
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxUrl"])
            .Returns("https://testoauth.expressauth.net/v2/tbsauth");
        _mockConfiguration.Setup(c => c["TaxBandits:ProductionUrl"])
            .Returns("https://oauth.expressauth.net/v2/tbsauth");
        
        _service = new TaxBanditsService(_httpClient, _mockLogger.Object, _mockConfiguration.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
    }

    [Test]
    public async Task GetAccessTokenAsync_ReturnsToken_WhenRequestSucceeds()
    {
        // Arrange
        var clientId = "test-client-id";
        var userToken = "test-user-token";
        var clientSecret = "test-secret";

        var expectedResponse = new TaxBanditsAuthResponse
        {
            StatusCode = 200,
            StatusName = "Success",
            StatusMessage = "Token generated successfully",
            AccessToken = "test-access-token",
            TokenType = "Bearer",
            ExpiresIn = 3600
        };

        var responseJson = JsonSerializer.Serialize(expectedResponse);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri.ToString().Contains("testoauth.expressauth.net") &&
                    req.Headers.Contains("Authentication")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetAccessTokenAsync(clientId, userToken, clientSecret);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.AccessToken, Is.EqualTo("test-access-token"));
        Assert.That(result.TokenType, Is.EqualTo("Bearer"));
        Assert.That(result.ExpiresIn, Is.EqualTo(3600));
        Assert.That(result.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task GetAccessTokenAsync_UsesProductionUrl_WhenUseSandboxIsFalse()
    {
        // Arrange
        var clientId = "test-client-id";
        var userToken = "test-user-token";
        var clientSecret = "test-secret";

        var expectedResponse = new TaxBanditsAuthResponse
        {
            StatusCode = 200,
            AccessToken = "test-token",
            TokenType = "Bearer",
            ExpiresIn = 3600
        };

        var responseJson = JsonSerializer.Serialize(expectedResponse);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri.ToString().Contains("oauth.expressauth.net") &&
                    !req.RequestUri.ToString().Contains("testoauth")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetAccessTokenAsync(clientId, userToken, clientSecret, useSandbox: false);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.AccessToken, Is.EqualTo("test-token"));
    }

    [Test]
    public void GetAccessTokenAsync_ThrowsHttpRequestException_WhenRequestFails()
    {
        // Arrange
        var clientId = "test-client-id";
        var userToken = "test-user-token";
        var clientSecret = "test-secret";

        var httpResponse = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized"),
            ReasonPhrase = "Unauthorized"
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act & Assert
        var ex = Assert.ThrowsAsync<HttpRequestException>(async () =>
            await _service.GetAccessTokenAsync(clientId, userToken, clientSecret));

        Assert.That(ex.Message, Does.Contain("401"));
        Assert.That(ex.Message, Does.Contain("Unauthorized"));
    }

    [Test]
    public void GetAccessTokenAsync_ThrowsArgumentException_WhenClientIdIsEmpty()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.GetAccessTokenAsync("", "userToken", "secret"));

        Assert.That(ex.ParamName, Is.EqualTo("clientId"));
    }

    [Test]
    public void GetAccessTokenAsync_ThrowsArgumentException_WhenUserTokenIsEmpty()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.GetAccessTokenAsync("clientId", "", "secret"));

        Assert.That(ex.ParamName, Is.EqualTo("userToken"));
    }

    [Test]
    public void GetAccessTokenAsync_ThrowsArgumentException_WhenClientSecretIsEmpty()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.GetAccessTokenAsync("clientId", "userToken", ""));

        Assert.That(ex.ParamName, Is.EqualTo("clientSecret"));
    }

    [Test]
    public void CreateJwsHs256_GeneratesValidJwsToken()
    {
        // Arrange
        var clientId = "test-client-id";
        var userToken = "test-user-token";
        var clientSecret = "test-secret";
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Act
        var jws = TaxBanditsService.CreateJwsHs256(clientId, userToken, clientSecret, iat);

        // Assert
        Assert.That(jws, Is.Not.Null);
        Assert.That(jws, Is.Not.Empty);
        
        // JWS should have three parts separated by dots
        var parts = jws.Split('.');
        Assert.That(parts.Length, Is.EqualTo(3));
        
        // Each part should not be empty
        Assert.That(parts[0], Is.Not.Empty); // header
        Assert.That(parts[1], Is.Not.Empty); // payload
        Assert.That(parts[2], Is.Not.Empty); // signature
    }

    [Test]
    public void CreateJwsHs256_GeneratesConsistentTokenForSameInputs()
    {
        // Arrange
        var clientId = "test-client-id";
        var userToken = "test-user-token";
        var clientSecret = "test-secret";
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Act
        var jws1 = TaxBanditsService.CreateJwsHs256(clientId, userToken, clientSecret, iat);
        var jws2 = TaxBanditsService.CreateJwsHs256(clientId, userToken, clientSecret, iat);

        // Assert
        Assert.That(jws1, Is.EqualTo(jws2));
    }

    [Test]
    public void CreateJwsHs256_GeneratesDifferentTokenForDifferentTimestamps()
    {
        // Arrange
        var clientId = "test-client-id";
        var userToken = "test-user-token";
        var clientSecret = "test-secret";
        var iat1 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var iat2 = iat1 + 1;

        // Act
        var jws1 = TaxBanditsService.CreateJwsHs256(clientId, userToken, clientSecret, iat1);
        var jws2 = TaxBanditsService.CreateJwsHs256(clientId, userToken, clientSecret, iat2);

        // Assert
        Assert.That(jws1, Is.Not.EqualTo(jws2));
    }
}
