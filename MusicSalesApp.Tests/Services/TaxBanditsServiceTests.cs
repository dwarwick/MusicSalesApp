using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Protected;
using MusicSalesApp.Data;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class TaxBanditsServiceTests
{
    private Mock<HttpMessageHandler> _mockHttpMessageHandler;
    private HttpClient _httpClient;
    private Mock<Microsoft.Extensions.Logging.ILogger<TaxBanditsService>> _mockLogger;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<IDbContextFactory<AppDbContext>> _mockDbContextFactory;
    private Mock<IEmailService> _mockEmailService;
    private TaxBanditsService _service;

    [SetUp]
    public void SetUp()
    {
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpMessageHandler.Object);
        _mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<TaxBanditsService>>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockDbContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockEmailService = new Mock<IEmailService>();
        
        // Setup default configuration values
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxUrl"])
            .Returns("https://testoauth.expressauth.net/v2/tbsauth");
        _mockConfiguration.Setup(c => c["TaxBandits:ProductionUrl"])
            .Returns("https://oauth.expressauth.net/v2/tbsauth");
        
        _service = new TaxBanditsService(
            _httpClient, 
            _mockLogger.Object, 
            _mockConfiguration.Object,
            _mockDbContextFactory.Object,
            _mockEmailService.Object);
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

    [Test]
    public async Task RequestW9ByEmailAsync_ReturnsError_WhenEmailIsEmpty()
    {
        // Act
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.RequestW9ByEmailAsync(1, "", "https://example.com"));

        // Assert
        Assert.That(ex.ParamName, Is.EqualTo("email"));
    }

    [Test]
    public async Task RequestW9ByEmailAsync_ReturnsError_WhenConfigurationIsMissing()
    {
        // Arrange - Configuration returns null for required fields
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:BusinessId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:WebhookRef"]).Returns((string)null);
        
        // Setup the IConfigurationSection for GetValue<bool>
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);
        
        _mockEmailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.RequestW9ByEmailAsync(1, "test@example.com", "https://example.com");

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("configuration"));
    }

    [Test]
    public void DeleteW9Async_ThrowsArgumentException_WhenPayeeRefIsEmpty()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.DeleteW9Async(""));

        Assert.That(ex.ParamName, Is.EqualTo("payeeRef"));
    }

    [Test]
    public async Task DeleteW9Async_ReturnsError_WhenConfigurationIsMissing()
    {
        // Arrange - Configuration returns null for required fields
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:BusinessId"]).Returns((string)null);
        
        // Setup the IConfigurationSection for GetValue<bool>
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        // Act
        var result = await _service.DeleteW9Async("test@example.com");

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("configuration"));
    }

    [Test]
    public async Task DeleteW9Async_ReturnsSuccess_WhenApiReturnsSuccess()
    {
        // Arrange
        var clientId = "test-client-id";
        var userToken = "test-user-token";
        var clientSecret = "test-secret";
        var businessId = "test-business-id";

        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns(clientId);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns(clientSecret);
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns(userToken);
        _mockConfiguration.Setup(c => c["TaxBandits:BusinessId"]).Returns(businessId);
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxApiUrl"]).Returns("https://testapi.taxbandits.com/v1.7.3/");

        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        // First call returns auth token
        var authResponse = new TaxBanditsAuthResponse
        {
            StatusCode = 200,
            AccessToken = "test-access-token",
            TokenType = "Bearer",
            ExpiresIn = 3600
        };
        var authResponseJson = JsonSerializer.Serialize(authResponse);

        // Second call is the delete request - returns success
        var deleteResponseJson = "{}";

        var callCount = 0;
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Auth request
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(authResponseJson)
                    };
                }
                else
                {
                    // Delete request
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(deleteResponseJson)
                    };
                }
            });

        // Act
        var result = await _service.DeleteW9Async("test@example.com");

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorMessage, Is.Null);
    }

    [Test]
    public async Task DeleteW9Async_ReturnsError_WhenApiReturnsError()
    {
        // Arrange
        var clientId = "test-client-id";
        var userToken = "test-user-token";
        var clientSecret = "test-secret";
        var businessId = "test-business-id";

        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns(clientId);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns(clientSecret);
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns(userToken);
        _mockConfiguration.Setup(c => c["TaxBandits:BusinessId"]).Returns(businessId);
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxApiUrl"]).Returns("https://testapi.taxbandits.com/v1.7.3/");

        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        // First call returns auth token
        var authResponse = new TaxBanditsAuthResponse
        {
            StatusCode = 200,
            AccessToken = "test-access-token",
            TokenType = "Bearer",
            ExpiresIn = 3600
        };
        var authResponseJson = JsonSerializer.Serialize(authResponse);

        // Second call is the delete request - returns error
        var deleteResponseJson = """{"Errors":[{"Id":"ERR-001","Message":"Record not found"}]}""";

        var callCount = 0;
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Auth request
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(authResponseJson)
                    };
                }
                else
                {
                    // Delete request - HTTP success but contains error in body
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(deleteResponseJson)
                    };
                }
            });

        // Act
        var result = await _service.DeleteW9Async("test@example.com");

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Record not found"));
    }
}
