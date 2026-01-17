using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Controllers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class TaxBanditsControllerTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockDbContextFactory;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<RoleManager<IdentityRole<int>>> _mockRoleManager;
    private Mock<ICreatorService> _mockCreatorService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<TaxBanditsController>> _mockLogger;
    private TaxBanditsController _controller;

    [SetUp]
    public void SetUp()
    {
        _mockDbContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        
        // Setup UserManager mock
        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null, null, null, null, null, null, null, null);
        
        // Setup RoleManager mock
        var roleStore = new Mock<IRoleStore<IdentityRole<int>>>();
        _mockRoleManager = new Mock<RoleManager<IdentityRole<int>>>(
            roleStore.Object, null, null, null, null);
        
        _mockCreatorService = new Mock<ICreatorService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<TaxBanditsController>>();

        _controller = new TaxBanditsController(
            _mockDbContextFactory.Object,
            _mockUserManager.Object,
            _mockRoleManager.Object,
            _mockCreatorService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task HandleW9CompleteWebhook_ReturnsUnauthorized_WhenSignatureInvalid()
    {
        // Arrange
        var webhookBody = @"{""FormType"":""FormW9"",""FormW9"":{""W9Status"":""COMPLETED""}}";
        
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns("test-client-id");
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns("test-secret");

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookBody));
        context.Request.ContentLength = webhookBody.Length;
        // No signature headers set - should fail validation
        
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        // Act
        var result = await _controller.HandleW9CompleteWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task HandleW9CompleteWebhook_ReturnsBadRequest_WhenJsonInvalid()
    {
        // Arrange
        var invalidJson = "not valid json";
        
        // Skip signature verification by not configuring credentials
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(invalidJson));
        context.Request.ContentLength = invalidJson.Length;
        
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        // Act
        var result = await _controller.HandleW9CompleteWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task HandleW9CompleteWebhook_ReturnsBadRequest_WhenFormTypeMissing()
    {
        // Arrange
        var webhookBody = @"{""SomeOtherField"":""value""}";
        
        // Skip signature verification
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookBody));
        context.Request.ContentLength = webhookBody.Length;
        
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        // Act
        var result = await _controller.HandleW9CompleteWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task HandleW9CompleteWebhook_ReturnsOkIgnored_WhenUnknownFormType()
    {
        // Arrange
        var webhookBody = @"{""FormType"":""UnknownForm""}";
        
        // Skip signature verification
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookBody));
        context.Request.ContentLength = webhookBody.Length;
        
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        // Act
        var result = await _controller.HandleW9CompleteWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }
}
