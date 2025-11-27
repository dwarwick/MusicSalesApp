using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Net;
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
    private Mock<RoleManager<IdentityRole<int>>> _mockRoleManager;
    private StubHttpMessageHandler _stubHandler;
    private HttpClient _httpClient;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<AuthenticationService>>();
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null, null, null, null, null, null, null, null);

        var contextAccessor = new Mock<IHttpContextAccessor>();
        var claimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        _mockSignInManager = new Mock<SignInManager<ApplicationUser>>(
            _mockUserManager.Object,
            contextAccessor.Object,
            claimsFactory.Object,
            null, null, null, null);

        var roleStore = new Mock<IRoleStore<IdentityRole<int>>>();
        _mockRoleManager = new Mock<RoleManager<IdentityRole<int>>>(roleStore.Object, null, null, null, null);

        _stubHandler = new StubHttpMessageHandler();
        _httpClient = new HttpClient(_stubHandler) { BaseAddress = new Uri("http://localhost/") };

        _serverAuthStateProvider = new ServerAuthenticationStateProvider(_mockHttpContextAccessor.Object);

        _service = new AuthenticationService(
            _serverAuthStateProvider,
            _mockUserManager.Object,
            _mockSignInManager.Object,
            _mockRoleManager.Object,
            _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _stubHandler.Dispose();
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
    public async Task LogoutAsync_WhenExceptionThrown_DoesNotThrow()
    {
        // Arrange
        _mockSignInManager.Setup(sm => sm.SignOutAsync()).ThrowsAsync(new Exception("boom"));

        // Act & Assert
        Assert.DoesNotThrowAsync(async () => await _service.LogoutAsync());
    }

    [Test]
    public async Task IsAuthenticatedAsync_WhenNotAuthenticated_ReturnsFalse()
    {
        // Arrange
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns((HttpContext)null);

        // Act
        var result = await _service.IsAuthenticatedAsync();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsAuthenticatedAsync_WhenAuthenticated_ReturnsTrue()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("name", "test") }, "Auth"));
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns(httpContext);

        // Act
        var result = await _service.IsAuthenticatedAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    private class StubHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new HttpResponseMessage(HttpStatusCode.OK);
        public Exception ThrowException { get; set; } = null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ThrowException != null)
            {
                throw ThrowException;
            }
            return Task.FromResult(Response);
        }
    }
}
