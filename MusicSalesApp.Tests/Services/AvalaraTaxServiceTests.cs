#nullable enable
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Protected;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AvalaraTaxServiceTests
{
    private Mock<HttpMessageHandler> _mockHttpMessageHandler;
    private HttpClient _httpClient;
    private Mock<Microsoft.Extensions.Logging.ILogger<AvalaraTaxService>> _mockLogger;
    private Mock<IConfiguration> _mockConfiguration;
    private AvalaraTaxService _service;

    [SetUp]
    public void SetUp()
    {
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpMessageHandler.Object);
        _mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<AvalaraTaxService>>();
        _mockConfiguration = new Mock<IConfiguration>();
        
        // Setup default configuration values
        _mockConfiguration.Setup(c => c["Avalara:SandboxTokenUrl"])
            .Returns("https://ai-sbx.avlr.sh/connect/token");
        _mockConfiguration.Setup(c => c["Avalara:ProductionTokenUrl"])
            .Returns("https://ai.avlr.sh/connect/token");
        
        _service = new AvalaraTaxService(
            _httpClient, 
            _mockLogger.Object, 
            _mockConfiguration.Object);
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
        var clientSecret = "test-client-secret";

        var tokenResponse = new
        {
            access_token = "test-access-token",
            token_type = "Bearer",
            expires_in = 3600
        };

        var responseJson = JsonSerializer.Serialize(tokenResponse);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("ai-sbx.avlr.sh")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetAccessTokenAsync(clientId, clientSecret);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True);
        Assert.That(result.AccessToken, Is.EqualTo("test-access-token"));
        Assert.That(result.TokenType, Is.EqualTo("Bearer"));
        Assert.That(result.ExpiresIn, Is.EqualTo(3600));
    }

    [Test]
    public async Task GetAccessTokenAsync_UsesProductionUrl_WhenUseSandboxIsFalse()
    {
        // Arrange
        var clientId = "test-client-id";
        var clientSecret = "test-client-secret";

        var tokenResponse = new
        {
            access_token = "test-access-token",
            token_type = "Bearer",
            expires_in = 3600
        };

        var responseJson = JsonSerializer.Serialize(tokenResponse);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("ai.avlr.sh") &&
                    !req.RequestUri!.ToString().Contains("ai-sbx")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetAccessTokenAsync(clientId, clientSecret, useSandbox: false);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True);
        Assert.That(result.AccessToken, Is.EqualTo("test-access-token"));
    }

    [Test]
    public async Task GetAccessTokenAsync_ReturnsError_WhenRequestFails()
    {
        // Arrange
        var clientId = "test-client-id";
        var clientSecret = "test-client-secret";

        var httpResponse = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Invalid credentials"),
            ReasonPhrase = "Unauthorized"
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetAccessTokenAsync(clientId, clientSecret);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Avalara auth failed"));
        Assert.That(result.ErrorMessage, Does.Contain("401"));
    }

    [Test]
    public void GetAccessTokenAsync_ThrowsArgumentException_WhenClientIdIsEmpty()
    {
        // Arrange
        var clientId = "";
        var clientSecret = "test-client-secret";

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.GetAccessTokenAsync(clientId, clientSecret));
    }

    [Test]
    public void GetAccessTokenAsync_ThrowsArgumentException_WhenClientSecretIsEmpty()
    {
        // Arrange
        var clientId = "test-client-id";
        var clientSecret = "";

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.GetAccessTokenAsync(clientId, clientSecret));
    }

    [Test]
    public async Task GetAccessTokenAsync_WithoutParams_UsesConfiguration()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["Avalara:ClientId"]).Returns("config-client-id");
        _mockConfiguration.Setup(c => c["Avalara:ClientSecret"]).Returns("config-client-secret");
        
        // Setup GetValue<bool> for UseSandbox
        var useSandboxSection = new Mock<IConfigurationSection>();
        useSandboxSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("Avalara:UseSandbox")).Returns(useSandboxSection.Object);

        var tokenResponse = new
        {
            access_token = "config-based-token",
            token_type = "Bearer",
            expires_in = 3600
        };

        var responseJson = JsonSerializer.Serialize(tokenResponse);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetAccessTokenAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True);
        Assert.That(result.AccessToken, Is.EqualTo("config-based-token"));
    }

    [Test]
    public async Task GetAccessTokenAsync_WithoutParams_ReturnsError_WhenConfigurationIncomplete()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["Avalara:ClientId"]).Returns((string?)null);
        _mockConfiguration.Setup(c => c["Avalara:ClientSecret"]).Returns((string?)null);
        
        // Setup GetValue<bool> for UseSandbox to avoid NullReferenceException
        var useSandboxSection = new Mock<IConfigurationSection>();
        useSandboxSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("Avalara:UseSandbox")).Returns(useSandboxSection.Object);

        // Act
        var result = await _service.GetAccessTokenAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("configuration is incomplete"));
    }

    [Test]
    public async Task GetAccessTokenAsync_SendsCorrectFormData()
    {
        // Arrange
        var clientId = "my-client-id";
        var clientSecret = "my-client-secret";

        string? capturedContent = null;

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>(async (request, _) =>
            {
                capturedContent = await request.Content!.ReadAsStringAsync();
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    access_token = "token",
                    token_type = "Bearer",
                    expires_in = 3600
                }))
            });

        // Act
        await _service.GetAccessTokenAsync(clientId, clientSecret);

        // Assert
        Assert.That(capturedContent, Is.Not.Null);
        Assert.That(capturedContent, Does.Contain("grant_type=client_credentials"));
        Assert.That(capturedContent, Does.Contain("client_id=my-client-id"));
        Assert.That(capturedContent, Does.Contain("client_secret=my-client-secret"));
    }

    [Test]
    public async Task GetAccessTokenAsync_ReturnsRawResponse()
    {
        // Arrange
        var clientId = "test-client-id";
        var clientSecret = "test-client-secret";

        var tokenResponse = new
        {
            access_token = "test-token",
            token_type = "Bearer",
            expires_in = 3600
        };

        var responseJson = JsonSerializer.Serialize(tokenResponse);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetAccessTokenAsync(clientId, clientSecret);

        // Assert
        Assert.That(result.RawResponse, Is.Not.Null);
        Assert.That(result.RawResponse, Does.Contain("access_token"));
    }
}
