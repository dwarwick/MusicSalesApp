using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using MusicSalesApp.Controllers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class AuthControllerTests
{
    private Mock<IConfiguration> _mockConfiguration;
    private AppDbContext _dbContext;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<SignInManager<ApplicationUser>> _mockSignInManager;
    private AuthController _controller;

    [SetUp]
    public void SetUp()
    {
        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration.Setup(c => c.GetSection("Auth:ExpireMinutes").Value).Returns("300");
        
        // Create in-memory database context
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        
        // Mock UserManager
        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null, null, null, null, null, null, null, null);
        
        // Mock SignInManager
        var contextAccessor = new Mock<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        contextAccessor.Setup(x => x.HttpContext).Returns(httpContext);
        
        var claimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        _mockSignInManager = new Mock<SignInManager<ApplicationUser>>(
            _mockUserManager.Object,
            contextAccessor.Object,
            claimsFactory.Object,
            null, null, null, null);
        
        _controller = new AuthController(
            _mockConfiguration.Object,
            _dbContext,
            _mockUserManager.Object,
            _mockSignInManager.Object);
        
        // Set HttpContext for the controller
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext?.Dispose();
    }

    [Test]
    public void Login_WithEmptyUsername_ReturnsBadRequest()
    {
        // Arrange
        var request = new LoginRequest { Username = "", Password = "password" };

        // Act
        var result = _controller.Login(request).Result;

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void Login_WithEmptyPassword_ReturnsBadRequest()
    {
        // Arrange
        var request = new LoginRequest { Username = "admin", Password = "" };

        // Act
        var result = _controller.Login(request).Result;

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void Login_WithNullUsername_ReturnsBadRequest()
    {
        // Arrange
        var request = new LoginRequest { Username = null, Password = "password" };

        // Act
        var result = _controller.Login(request).Result;

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void Login_WithNullPassword_ReturnsBadRequest()
    {
        // Arrange
        var request = new LoginRequest { Username = "admin", Password = null };

        // Act
        var result = _controller.Login(request).Result;

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void LoginRequest_DefaultConstructor_InitializesProperties()
    {
        // Act
        var request = new LoginRequest();

        // Assert
        Assert.That(request.Username, Is.EqualTo(string.Empty));
        Assert.That(request.Password, Is.EqualTo(string.Empty));
    }
}
