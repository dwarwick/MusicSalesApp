using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Services;
using System.Security.Claims;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AuthenticationServiceTests
{
    private Mock<IJSRuntime> _mockJSRuntime;
    private Mock<ILogger<AuthenticationService>> _mockLogger;
    private Mock<IHttpContextAccessor> _mockHttpContextAccessor;
    private ServerAuthenticationStateProvider _serverAuthStateProvider;
    private AuthenticationService _service;

    [SetUp]
    public void SetUp()
    {
        _mockJSRuntime = new Mock<IJSRuntime>();
        _mockLogger = new Mock<ILogger<AuthenticationService>>();
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        
        _serverAuthStateProvider = new ServerAuthenticationStateProvider(_mockHttpContextAccessor.Object);
        
        _service = new AuthenticationService(
            _serverAuthStateProvider,
            _mockJSRuntime.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task LoginAsync_WithEmptyUsername_ReturnsFalse()
    {
        // Act
        var result = await _service.LoginAsync("", "password");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task LoginAsync_WithEmptyPassword_ReturnsFalse()
    {
        // Act
        var result = await _service.LoginAsync("admin", "");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task LoginAsync_WhenJSReturnsTrue_ReturnsTrue()
    {
        // Arrange
        _mockJSRuntime
            .Setup(js => js.InvokeAsync<bool>("loginUser", It.IsAny<object[]>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.LoginAsync("admin", "password");

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task LoginAsync_WhenJSReturnsFalse_ReturnsFalse()
    {
        // Arrange
        _mockJSRuntime
            .Setup(js => js.InvokeAsync<bool>("loginUser", It.IsAny<object[]>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.LoginAsync("admin", "password");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task LoginAsync_WhenExceptionThrown_ReturnsFalse()
    {
        // Arrange
        _mockJSRuntime
            .Setup(js => js.InvokeAsync<bool>("loginUser", It.IsAny<object[]>()))
            .ThrowsAsync(new Exception("JS Error"));

        // Act
        var result = await _service.LoginAsync("admin", "password");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task LogoutAsync_CallsJSRuntime()
    {
        // Arrange
        _mockJSRuntime
            .Setup(js => js.InvokeAsync<object>("logoutUser", It.IsAny<object[]>()))
            .ReturnsAsync((object)null);

        // Act
        await _service.LogoutAsync();

        // Assert
        _mockJSRuntime.Verify(
            js => js.InvokeAsync<object>("logoutUser", It.IsAny<object[]>()),
            Times.Once);
    }

    [Test]
    public async Task LogoutAsync_CompletesSuccessfully()
    {
        // Arrange
        _mockJSRuntime
            .Setup(js => js.InvokeAsync<object>("logoutUser", It.IsAny<object[]>()))
            .ReturnsAsync((object)null);

        // Act & Assert
        Assert.DoesNotThrowAsync(async () => await _service.LogoutAsync());
    }

    [Test]
    public async Task LogoutAsync_WhenExceptionThrown_DoesNotThrow()
    {
        // Arrange
        _mockJSRuntime
            .Setup(js => js.InvokeAsync<object>("logoutUser", It.IsAny<object[]>()))
            .ThrowsAsync(new Exception("JS Error"));

        // Act & Assert
        Assert.DoesNotThrowAsync(async () => await _service.LogoutAsync());
    }

    [Test]
    public async Task GetCurrentUserAsync_WhenNotAuthenticated_ReturnsAnonymousUser()
    {
        // Arrange
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns((HttpContext)null);

        // Act
        var result = await _service.GetCurrentUserAsync();

        // Assert
        Assert.That(result.Identity.IsAuthenticated, Is.False);
    }

    [Test]
    public async Task IsAuthenticatedAsync_WhenUserIsAuthenticated_ReturnsTrue()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity(new[] { new Claim("name", "test") }, "TestAuth");
        httpContext.User = new ClaimsPrincipal(identity);
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns(httpContext);

        // Act
        var result = await _service.IsAuthenticatedAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsAuthenticatedAsync_WhenUserIsNotAuthenticated_ReturnsFalse()
    {
        // Arrange
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns((HttpContext)null);

        // Act
        var result = await _service.IsAuthenticatedAsync();

        // Assert
        Assert.That(result, Is.False);
    }
}
