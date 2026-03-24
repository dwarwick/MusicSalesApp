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

    [Test]
    public async Task ReportForm1099TransactionsBatchAsync_ReturnsSuccess_WhenStatusMsgIsTransactionsSavedSuccessfully()
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

        var authResponse = new TaxBanditsAuthResponse
        {
            StatusCode = 200,
            AccessToken = "test-access-token",
            TokenType = "Bearer",
            ExpiresIn = 3600
        };
        var authResponseJson = JsonSerializer.Serialize(authResponse);

        // Success response with the expected StatusMsg
        var form1099ResponseJson = """
        {
            "SubmissionId": "c1c5670b-5af2-49c5-9f70-8537a89a1c3b",
            "StatusMsg": "Transactions saved successfully",
            "StatusTs": "2026-01-19 15:03:39 -05:00",
            "Errors": null
        }
        """;

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
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(authResponseJson)
                    };
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(form1099ResponseJson)
                    };
                }
            });

        var transactions = new List<Form1099Transaction>
        {
            new() { PayeeRef = "test@example.com", SequenceId = "TXN-001", TransactionDate = DateTime.UtcNow, GrossAmount = 100m, WithheldAmount = 0m }
        };

        // Act
        var result = await _service.ReportForm1099TransactionsBatchAsync(transactions);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.TransactionId, Is.EqualTo("c1c5670b-5af2-49c5-9f70-8537a89a1c3b"));
        Assert.That(result.StatusMessage, Is.EqualTo("Transactions saved successfully"));
        Assert.That(result.ErrorMessage, Is.Null);

        // Verify no admin email was sent
        _mockEmailService.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.Is<string>(s => s.Contains("Failed")), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task ReportForm1099TransactionsBatchAsync_ReturnsFailure_WhenStatusMsgIsNotSuccess()
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
        _mockConfiguration.Setup(c => c["EmailSettings:CustomerServiceEmail"]).Returns("admin@streamtunes.net");

        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        _mockEmailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var authResponse = new TaxBanditsAuthResponse
        {
            StatusCode = 200,
            AccessToken = "test-access-token",
            TokenType = "Bearer",
            ExpiresIn = 3600
        };
        var authResponseJson = JsonSerializer.Serialize(authResponse);

        // Failure response with a different StatusMsg
        var form1099ResponseJson = """
        {
            "SubmissionId": "c1c5670b-5af2-49c5-9f70-8537a89a1c3b",
            "StatusMsg": "Some transactions failed validation",
            "StatusTs": "2026-01-19 15:03:39 -05:00",
            "Errors": null
        }
        """;

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
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(authResponseJson)
                    };
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(form1099ResponseJson)
                    };
                }
            });

        var transactions = new List<Form1099Transaction>
        {
            new() { PayeeRef = "test@example.com", SequenceId = "TXN-001", TransactionDate = DateTime.UtcNow, GrossAmount = 100m, WithheldAmount = 0m }
        };

        // Act
        var result = await _service.ReportForm1099TransactionsBatchAsync(transactions);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.TransactionId, Is.EqualTo("c1c5670b-5af2-49c5-9f70-8537a89a1c3b"));
        Assert.That(result.StatusMessage, Is.EqualTo("Some transactions failed validation"));
        Assert.That(result.ErrorMessage, Is.EqualTo("Some transactions failed validation"));

        // Verify admin email was sent with the endpoint and submission ID
        _mockEmailService.Verify(
            e => e.SendEmailAsync(
                "admin@streamtunes.net",
                It.Is<string>(s => s.Contains("Failed")),
                It.Is<string>(body => 
                    body.Contains("Form1099Transactions") && 
                    body.Contains("c1c5670b-5af2-49c5-9f70-8537a89a1c3b"))),
            Times.Once);
    }

    [Test]
    public async Task ReportForm1099TransactionsBatchAsync_SendsAdminEmail_WhenErrorsInResponse()
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
        _mockConfiguration.Setup(c => c["EmailSettings:CustomerServiceEmail"]).Returns("admin@streamtunes.net");

        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        _mockEmailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var authResponse = new TaxBanditsAuthResponse
        {
            StatusCode = 200,
            AccessToken = "test-access-token",
            TokenType = "Bearer",
            ExpiresIn = 3600
        };
        var authResponseJson = JsonSerializer.Serialize(authResponse);

        // Response with errors
        var form1099ResponseJson = """
        {
            "SubmissionId": "abc123",
            "StatusMsg": "Error",
            "Errors": [{"Id": "ERR-001", "Message": "Invalid PayeeRef"}]
        }
        """;

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
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(authResponseJson)
                    };
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(form1099ResponseJson)
                    };
                }
            });

        var transactions = new List<Form1099Transaction>
        {
            new() { PayeeRef = "test@example.com", SequenceId = "TXN-001", TransactionDate = DateTime.UtcNow, GrossAmount = 100m, WithheldAmount = 0m }
        };

        // Act
        var result = await _service.ReportForm1099TransactionsBatchAsync(transactions);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Invalid PayeeRef"));

        // Verify admin email was sent
        _mockEmailService.Verify(
            e => e.SendEmailAsync(
                "admin@streamtunes.net",
                It.Is<string>(s => s.Contains("Failed")),
                It.Is<string>(body => body.Contains("Invalid PayeeRef"))),
            Times.Once);
    }

    [Test]
    public async Task ReportForm1099TransactionsBatchAsync_SendsAdminEmail_WhenHttpError()
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
        _mockConfiguration.Setup(c => c["EmailSettings:CustomerServiceEmail"]).Returns("admin@streamtunes.net");

        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        _mockEmailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var authResponse = new TaxBanditsAuthResponse
        {
            StatusCode = 200,
            AccessToken = "test-access-token",
            TokenType = "Bearer",
            ExpiresIn = 3600
        };
        var authResponseJson = JsonSerializer.Serialize(authResponse);

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
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(authResponseJson)
                    };
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("Internal Server Error")
                    };
                }
            });

        var transactions = new List<Form1099Transaction>
        {
            new() { PayeeRef = "test@example.com", SequenceId = "TXN-001", TransactionDate = DateTime.UtcNow, GrossAmount = 100m, WithheldAmount = 0m }
        };

        // Act
        var result = await _service.ReportForm1099TransactionsBatchAsync(transactions);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("HTTP 500"));

        // Verify admin email was sent
        _mockEmailService.Verify(
            e => e.SendEmailAsync(
                "admin@streamtunes.net",
                It.Is<string>(s => s.Contains("Failed")),
                It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task ReportForm1099TransactionsBatchAsync_ReturnsEmptySuccess_WhenNoTransactions()
    {
        // Arrange - empty transaction list
        var transactions = new List<Form1099Transaction>();

        // Act
        var result = await _service.ReportForm1099TransactionsBatchAsync(transactions);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.StatusMessage, Is.EqualTo("No transactions to report"));

        // Verify no email was sent
        _mockEmailService.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    #region GetTransientTokenAsync Tests

    [Test]
    public void GetTransientTokenAsync_ThrowsArgumentNullException_WhenOriginsIsNull()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _service.GetTransientTokenAsync(null!));

        Assert.That(ex.ParamName, Is.EqualTo("origins"));
    }

    [Test]
    public void GetTransientTokenAsync_ThrowsArgumentException_WhenOriginsIsEmpty()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.GetTransientTokenAsync(new List<string>()));

        Assert.That(ex.ParamName, Is.EqualTo("origins"));
    }

    [Test]
    public async Task GetTransientTokenAsync_ReturnsError_WhenConfigurationIsMissing()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns((string)null);
        
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        // Act
        var result = await _service.GetTransientTokenAsync(new List<string> { "https://example.com" });

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("configuration"));
    }

    [Test]
    public async Task GetTransientTokenAsync_ReturnsToken_WhenRequestSucceeds()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns("test-client-id");
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns("test-secret");
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns("test-user-token");
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxAuthUrl"]).Returns("https://testoauth.expressauth.net/v2/tbsauth");
        
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        var tokenResponse = new
        {
            StatusCode = 200,
            StatusName = "OK",
            StatusMessage = "Successful API call",
            TransientToken = "test-transient-token",
            TokenType = "Bearer",
            ExpiresIn = 900,
            Errors = (object)null
        };

        var responseJson = JsonSerializer.Serialize(tokenResponse);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("transienttoken")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetTransientTokenAsync(new List<string> { "https://example.com" });

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.TransientToken, Is.EqualTo("test-transient-token"));
        Assert.That(result.TokenType, Is.EqualTo("Bearer"));
        Assert.That(result.ExpiresIn, Is.EqualTo(900));
    }

    [Test]
    public async Task GetTransientTokenAsync_ReturnsError_WhenHttpRequestFails()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns("test-client-id");
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns("test-secret");
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns("test-user-token");
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxAuthUrl"]).Returns("https://testoauth.expressauth.net/v2/tbsauth");
        
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        var httpResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"StatusMessage\": \"Internal server error\"}")
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("transienttoken")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetTransientTokenAsync(new List<string> { "https://example.com" });

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GetTransientTokenAsync_ReturnsError_WhenResponseHasErrors()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns("test-client-id");
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns("test-secret");
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns("test-user-token");
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxAuthUrl"]).Returns("https://testoauth.expressauth.net/v2/tbsauth");
        
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        var errorResponse = new
        {
            StatusCode = 400,
            Errors = new[]
            {
                new { Id = "ERR001", Name = "InvalidOrigin", Message = "Origin not allowed" }
            }
        };

        var responseJson = JsonSerializer.Serialize(errorResponse);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("transienttoken")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetTransientTokenAsync(new List<string> { "https://example.com" });

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Origin not allowed"));
    }

    #endregion

    #region RequestInstantTinMatchAsync Tests

    [Test]
    public void RequestInstantTinMatchAsync_ThrowsArgumentNullException_WhenRequestIsNull()
    {
        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(() => 
            _service.RequestInstantTinMatchAsync(null!));
    }

    [Test]
    public void RequestInstantTinMatchAsync_ThrowsArgumentException_WhenTINIsEmpty()
    {
        // Arrange
        var request = new InstantTinMatchRequest
        {
            TINType = "SSN",
            TIN = "",
            FirstNm = "John",
            LastNm = "Doe",
            UserId = 1
        };

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(() => 
            _service.RequestInstantTinMatchAsync(request));
    }

    [Test]
    public void RequestInstantTinMatchAsync_ThrowsArgumentException_WhenTINTypeIsEmpty()
    {
        // Arrange
        var request = new InstantTinMatchRequest
        {
            TINType = "",
            TIN = "123-45-6789",
            FirstNm = "John",
            LastNm = "Doe",
            UserId = 1
        };

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(() => 
            _service.RequestInstantTinMatchAsync(request));
    }

    [Test]
    public async Task RequestInstantTinMatchAsync_ReturnsError_WhenConfigurationIsMissing()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns((string)null);

        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);

        var request = new InstantTinMatchRequest
        {
            TINType = "SSN",
            TIN = "123-45-6789",
            FirstNm = "John",
            LastNm = "Doe",
            UserId = 1
        };

        // Act
        var result = await _service.RequestInstantTinMatchAsync(request);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("configuration"));
    }

    [Test]
    public async Task RequestInstantTinMatchAsync_ParsesErrorsArray_WhenHttpNonSuccess()
    {
        // Arrange — simulate a 400 Bad Request response with Errors array
        // (similar to TaxBandits returning validation errors like invalid middle name)
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns("test-client");
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns("test-secret");
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns("test-token");
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxApiUrl"]).Returns("https://testapi.taxbandits.com/v2.0");

        var authResponseJson = JsonSerializer.Serialize(new { AccessToken = "test-token", TokenType = "Bearer", ExpiresIn = 3600, StatusCode = 200 });
        var tinMatchErrorJson = JsonSerializer.Serialize(new
        {
            StatusCode = 400,
            StatusName = "BadRequest",
            StatusMessage = "Validation error has occurred",
            Errors = new[]
            {
                new { Id = "F72-100162", Name = "MiddleNm", Message = "Middle Name is Invalid. The Middle Name can have Alphabets, Numbers and Special Characters ( & - ).  Other special characters are not allowed." }
            }
        });

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
                    // First call: auth request
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(authResponseJson)
                    };
                }
                else
                {
                    // Second call: TIN match request returns 400
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(tinMatchErrorJson)
                    };
                }
            });

        var request = new InstantTinMatchRequest
        {
            TINType = "SSN",
            TIN = "123-45-6789",
            FirstNm = "John",
            MiddleNm = "A.",
            LastNm = "Doe",
            UserId = 1
        };

        // Act
        var result = await _service.RequestInstantTinMatchAsync(request);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Middle Name is Invalid"));
        Assert.That(result.ErrorMessage, Does.Not.Contain("HTTP 400"));
    }

    [Test]
    public async Task RequestInstantTinMatchAsync_JoinsMultipleErrors_WhenHttpNonSuccess()
    {
        // Arrange — simulate a 400 response with multiple Errors
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns("test-client");
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns("test-secret");
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns("test-token");
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxApiUrl"]).Returns("https://testapi.taxbandits.com/v2.0");

        var authResponseJson = JsonSerializer.Serialize(new { AccessToken = "test-token", TokenType = "Bearer", ExpiresIn = 3600, StatusCode = 200 });
        var tinMatchErrorJson = JsonSerializer.Serialize(new
        {
            StatusCode = 400,
            StatusName = "BadRequest",
            StatusMessage = "Validation error has occurred",
            Errors = new[]
            {
                new { Id = "E1", Name = "MiddleNm", Message = "Middle Name is Invalid." },
                new { Id = "E2", Name = "FirstNm", Message = "First Name is required." }
            }
        });

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
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(authResponseJson)
                    };
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(tinMatchErrorJson)
                    };
                }
            });

        var request = new InstantTinMatchRequest
        {
            TINType = "SSN",
            TIN = "123-45-6789",
            FirstNm = "John",
            LastNm = "Doe",
            UserId = 1
        };

        // Act
        var result = await _service.RequestInstantTinMatchAsync(request);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Middle Name is Invalid."));
        Assert.That(result.ErrorMessage, Does.Contain("First Name is required."));
        Assert.That(result.ErrorMessage, Does.Contain(" | "));
    }

    [Test]
    public async Task RequestInstantTinMatchAsync_FallsBackToStatusMessage_WhenNoErrorsArray()
    {
        // Arrange — simulate a 400 response with StatusMessage but no Errors array
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns("test-client");
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns("test-secret");
        _mockConfiguration.Setup(c => c["TaxBandits:UserToken"]).Returns("test-token");
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        _mockConfiguration.Setup(c => c.GetSection("TaxBandits:UseSandbox")).Returns(mockSection.Object);
        _mockConfiguration.Setup(c => c["TaxBandits:SandboxApiUrl"]).Returns("https://testapi.taxbandits.com/v2.0");

        var authResponseJson = JsonSerializer.Serialize(new { AccessToken = "test-token", TokenType = "Bearer", ExpiresIn = 3600, StatusCode = 200 });
        var errorJson = JsonSerializer.Serialize(new
        {
            StatusCode = 401,
            StatusMessage = "Unauthorized access"
        });

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
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(authResponseJson)
                    };
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent(errorJson)
                    };
                }
            });

        var request = new InstantTinMatchRequest
        {
            TINType = "SSN",
            TIN = "123-45-6789",
            FirstNm = "John",
            LastNm = "Doe",
            UserId = 1
        };

        // Act
        var result = await _service.RequestInstantTinMatchAsync(request);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Unauthorized access"));
    }

    #endregion
}
