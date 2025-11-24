using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Security.Claims;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AuthenticationServiceTests
{
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<SignInManager<ApplicationUser>> _mockSignInManager;
    private Mock<ILogger<AuthenticationService>> _mockLogger;
    private Mock<IHttpContextAccessor> _mockHttpContextAccessor;
    private ServerAuthenticationStateProvider _serverAuthStateProvider;
    private AuthenticationService _service;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<AuthenticationService>>();
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        
        // Mock UserManager
        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null, null, null, null, null, null, null, null);
        
        // Mock SignInManager
        var contextAccessor = new Mock<IHttpContextAccessor>();
        var claimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        _mockSignInManager = new Mock<SignInManager<ApplicationUser>>(
            _mockUserManager.Object,
            contextAccessor.Object,
            claimsFactory.Object,
            null, null, null, null);
        
        _serverAuthStateProvider = new ServerAuthenticationStateProvider(_mockHttpContextAccessor.Object);
        
        _service = new AuthenticationService(
            _serverAuthStateProvider,
            _mockUserManager.Object,
            _mockSignInManager.Object,
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
    public async Task LoginAsync_WithValidCredentials_ReturnsTrue()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "admin", Email = "admin@app.com" };
        _mockUserManager.Setup(um => um.FindByEmailAsync("admin@app.com")).ReturnsAsync(user);
        _mockSignInManager
            .Setup(sm => sm.PasswordSignInAsync(user, "password", true, false))
            .ReturnsAsync(SignInResult.Success);

        // Act
        var result = await _service.LoginAsync("admin@app.com", "password");

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task LoginAsync_WithInvalidCredentials_ReturnsFalse()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "admin", Email = "admin@app.com" };
        _mockUserManager.Setup(um => um.FindByEmailAsync("admin@app.com")).ReturnsAsync(user);
        _mockSignInManager
            .Setup(sm => sm.PasswordSignInAsync(user, "wrongpassword", true, false))
            .ReturnsAsync(SignInResult.Failed);

        // Act
        var result = await _service.LoginAsync("admin@app.com", "wrongpassword");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task LoginAsync_WithNonExistentUser_ReturnsFalse()
    {
        // Arrange
        _mockUserManager.Setup(um => um.FindByEmailAsync("nonexistent@app.com")).ReturnsAsync((ApplicationUser)null);
        _mockUserManager.Setup(um => um.FindByNameAsync("nonexistent@app.com")).ReturnsAsync((ApplicationUser)null);

        // Act
        var result = await _service.LoginAsync("nonexistent@app.com", "password");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task LoginAsync_WhenExceptionThrown_ReturnsFalse()
    {
        // Arrange
        _mockUserManager
            .Setup(um => um.FindByEmailAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Database Error"));

        // Act
        var result = await _service.LoginAsync("admin", "password");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task LogoutAsync_CallsSignInManager()
    {
        // Arrange
        _mockSignInManager.Setup(sm => sm.SignOutAsync()).Returns(Task.CompletedTask);

        // Act
        await _service.LogoutAsync();

        // Assert
        _mockSignInManager.Verify(sm => sm.SignOutAsync(), Times.Once);
    }

    [Test]
    public async Task LogoutAsync_CompletesSuccessfully()
    {
        // Arrange
        _mockSignInManager.Setup(sm => sm.SignOutAsync()).Returns(Task.CompletedTask);

        // Act & Assert
        Assert.DoesNotThrowAsync(async () => await _service.LogoutAsync());
    }

    [Test]
    public async Task LogoutAsync_WhenExceptionThrown_DoesNotThrow()
    {
        // Arrange
        _mockSignInManager
            .Setup(sm => sm.SignOutAsync())
            .ThrowsAsync(new Exception("SignOut Error"));

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
