using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Controllers;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;
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
    private Mock<ICreatorEmailService> _mockCreatorEmailService;
    private Mock<ITaxBanditsService> _mockTaxBanditsService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<TaxBanditsController>> _mockLogger;
    private Mock<IHubContext<WebhookStatusHub>> _mockHubContext;
    private Mock<IAdminNotificationService> _mockAdminNotificationService;
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
        _mockCreatorEmailService = new Mock<ICreatorEmailService>();
        _mockTaxBanditsService = new Mock<ITaxBanditsService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<TaxBanditsController>>();
        _mockAdminNotificationService = new Mock<IAdminNotificationService>();
        
        // Setup HubContext mock
        _mockHubContext = new Mock<IHubContext<WebhookStatusHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        _controller = new TaxBanditsController(
            _mockDbContextFactory.Object,
            _mockUserManager.Object,
            _mockRoleManager.Object,
            _mockCreatorService.Object,
            _mockCreatorEmailService.Object,
            _mockTaxBanditsService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockHubContext.Object,
            _mockAdminNotificationService.Object);
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

    [Test]
    public async Task HandleTinMatchCompleteWebhook_ReturnsUnauthorized_WhenSignatureInvalid()
    {
        // Arrange
        var webhookBody = @"{""TINStatusCode"":""TIN-001"",""TINStatus"":""SUCCESS""}";
        
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
        var result = await _controller.HandleTinMatchCompleteWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task HandleTinMatchCompleteWebhook_ReturnsBadRequest_WhenJsonInvalid()
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
        var result = await _controller.HandleTinMatchCompleteWebhook();

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task HandleW9CompleteWebhook_CreatesW9Request_WhenDropInFlowAndCreatorMatchedByPayeeRef()
    {
        // Arrange - Set up in-memory database with a creator that has TaxBanditsPayeeRef
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TaxBanditsTest_W9DropIn_{Guid.NewGuid()}")
            .Options;

        // Seed the database
        using (var seedContext = new AppDbContext(options))
        {
            var user = new ApplicationUser { Id = 42, UserName = "creator@test.com", Email = "creator@test.com" };
            seedContext.Users.Add(user);
            seedContext.Creators.Add(new Creator
            {
                Id = 1,
                UserId = 42,
                TaxBanditsPayeeRef = "creator@test.com",
                TaxFormStatus = TaxFormStatus.Pending,
                OnboardingStatus = CreatorOnboardingStatus.Completed,
                PayPalAccountAffirmed = true,
                DisplayName = "Test Creator"
            });
            await seedContext.SaveChangesAsync();
        }

        var mockDbFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockDbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(options));

        // Skip signature verification
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);

        // Setup TIN match to return success
        _mockTaxBanditsService
            .Setup(s => s.RequestInstantTinMatchAsync(It.IsAny<InstantTinMatchRequest>()))
            .ReturnsAsync(new InstantTinMatchResponse { Success = true, TINStatusCode = "TIN-001", TINStatus = "SUCCESS" });

        // Setup creator service mocks for the completion flow
        var creatorAfterUpdate = new Creator
        {
            Id = 1,
            UserId = 42,
            TaxFormStatus = TaxFormStatus.Completed,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            PayPalAccountAffirmed = true,
            DisplayName = "Test Creator"
        };
        _mockCreatorService
            .Setup(s => s.GetCreatorByUserIdAsync(42))
            .ReturnsAsync(creatorAfterUpdate);
        _mockCreatorService
            .Setup(s => s.UpdateTaxFormStatusWithTaxDataAsync(
                It.IsAny<int>(), It.IsAny<TaxFormStatus>(), It.IsAny<TaxResidencyType>(),
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>(),
                It.IsAny<Guid?>(), It.IsAny<bool>()))
            .ReturnsAsync(creatorAfterUpdate);
        _mockCreatorService
            .Setup(s => s.UpdateOnboardingStatusAsync(It.IsAny<int>(), It.IsAny<CreatorOnboardingStatus>()))
            .ReturnsAsync(creatorAfterUpdate);

        _mockUserManager.Setup(u => u.FindByIdAsync("42"))
            .ReturnsAsync(new ApplicationUser { Id = 42, Email = "creator@test.com" });
        _mockUserManager.Setup(u => u.IsInRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        _mockRoleManager.Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockRoleManager.Setup(r => r.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(s => s.ToUpperInvariant());

        // Create the controller with in-memory db factory
        var controller = new TaxBanditsController(
            mockDbFactory.Object,
            _mockUserManager.Object,
            _mockRoleManager.Object,
            _mockCreatorService.Object,
            _mockCreatorEmailService.Object,
            _mockTaxBanditsService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockHubContext.Object,
            _mockAdminNotificationService.Object);

        // W-9 webhook body with FormData for TIN matching
        var webhookBody = @"{
            ""FormType"": ""FORMW9"",
            ""FormW9"": {
                ""SubmissionId"": ""sub-123"",
                ""PayeeRef"": ""creator@test.com"",
                ""W9Status"": ""COMPLETED"",
                ""RecipientId"": ""rec-1"",
                ""FormData"": {
                    ""TINType"": ""SSN"",
                    ""TIN"": ""123456789"",
                    ""FirstNm"": ""John"",
                    ""LastNm"": ""Doe""
                }
            }
        }";

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookBody));
        context.Request.ContentLength = webhookBody.Length;
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        // Act
        var result = await controller.HandleW9CompleteWebhook();

        // Assert - webhook should succeed (not return request_not_found)
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var statusProp = okResult.Value?.GetType().GetProperty("status");
        var statusValue = statusProp?.GetValue(okResult.Value)?.ToString();
        Assert.That(statusValue, Is.EqualTo("success"));

        // Verify W9Request was created in the database
        using var verifyContext = new AppDbContext(options);
        var w9Request = await verifyContext.W9Requests.FirstOrDefaultAsync(w => w.UserId == 42);
        Assert.That(w9Request, Is.Not.Null, "W9Request should have been created for Drop-in UI flow");
        Assert.That(w9Request!.Email, Is.EqualTo("creator@test.com"));
        Assert.That(w9Request.SubmissionId, Is.EqualTo("sub-123"));

        // Verify creator activation was attempted
        _mockCreatorService.Verify(s => s.ActivateCreatorAsync(It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task HandleW9CompleteWebhook_W8BEN_CreatesW9Request_WhenDropInFlowAndCreatorMatchedByPayeeRef()
    {
        // Arrange - Set up in-memory database with a creator for W-8BEN flow
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TaxBanditsTest_W8DropIn_{Guid.NewGuid()}")
            .Options;

        using (var seedContext = new AppDbContext(options))
        {
            var user = new ApplicationUser { Id = 50, UserName = "foreign@test.com", Email = "foreign@test.com" };
            seedContext.Users.Add(user);
            seedContext.Creators.Add(new Creator
            {
                Id = 2,
                UserId = 50,
                TaxBanditsPayeeRef = "foreign@test.com",
                TaxFormStatus = TaxFormStatus.Pending,
                OnboardingStatus = CreatorOnboardingStatus.Completed,
                PayPalAccountAffirmed = true,
                DisplayName = "Foreign Creator"
            });
            await seedContext.SaveChangesAsync();
        }

        var mockDbFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockDbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(options));

        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);

        var creatorAfterUpdate = new Creator
        {
            Id = 2,
            UserId = 50,
            TaxFormStatus = TaxFormStatus.Completed,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            PayPalAccountAffirmed = true,
            DisplayName = "Foreign Creator"
        };
        _mockCreatorService
            .Setup(s => s.GetCreatorByUserIdAsync(50))
            .ReturnsAsync(creatorAfterUpdate);
        _mockCreatorService
            .Setup(s => s.UpdateTaxFormStatusWithTaxDataAsync(
                It.IsAny<int>(), It.IsAny<TaxFormStatus>(), It.IsAny<TaxResidencyType>(),
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>(),
                It.IsAny<Guid?>(), It.IsAny<bool>()))
            .ReturnsAsync(creatorAfterUpdate);
        _mockCreatorService
            .Setup(s => s.UpdateOnboardingStatusAsync(It.IsAny<int>(), It.IsAny<CreatorOnboardingStatus>()))
            .ReturnsAsync(creatorAfterUpdate);

        _mockUserManager.Setup(u => u.FindByIdAsync("50"))
            .ReturnsAsync(new ApplicationUser { Id = 50, Email = "foreign@test.com" });
        _mockUserManager.Setup(u => u.IsInRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        _mockRoleManager.Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockRoleManager.Setup(r => r.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(s => s.ToUpperInvariant());

        var controller = new TaxBanditsController(
            mockDbFactory.Object,
            _mockUserManager.Object,
            _mockRoleManager.Object,
            _mockCreatorService.Object,
            _mockCreatorEmailService.Object,
            _mockTaxBanditsService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockHubContext.Object,
            _mockAdminNotificationService.Object);

        // W-8BEN webhook body
        var webhookBody = @"{
            ""FormType"": ""FORMW8BEN"",
            ""FormW8Ben"": {
                ""SubmissionId"": ""sub-456"",
                ""PayeeRef"": ""foreign@test.com"",
                ""W8BENStatus"": ""COMPLETED"",
                ""FormData"": {
                    ""CountryOfCitizenShip"": ""GB""
                }
            }
        }";

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookBody));
        context.Request.ContentLength = webhookBody.Length;
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        // Act
        var result = await controller.HandleW9CompleteWebhook();

        // Assert - webhook should succeed
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var statusProp = okResult.Value?.GetType().GetProperty("status");
        var statusValue = statusProp?.GetValue(okResult.Value)?.ToString();
        Assert.That(statusValue, Is.EqualTo("success"));

        // Verify W9Request was created
        using var verifyContext = new AppDbContext(options);
        var w9Request = await verifyContext.W9Requests.FirstOrDefaultAsync(w => w.UserId == 50);
        Assert.That(w9Request, Is.Not.Null, "W9Request should have been created for Drop-in UI W-8BEN flow");
        Assert.That(w9Request!.Email, Is.EqualTo("foreign@test.com"));

        // Verify creator activation was attempted
        _mockCreatorService.Verify(s => s.ActivateCreatorAsync(It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task HandleW9CompleteWebhook_BroadcastsGenericMessage_WhenTinMatchApiFails()
    {
        // Arrange - set up in-memory DB with a creator and existing W9Request
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TaxBanditsTest_TinMatchFail_{Guid.NewGuid()}")
            .Options;

        using (var seedContext = new AppDbContext(options))
        {
            var user = new ApplicationUser { Id = 99, UserName = "tinmatch@test.com", Email = "tinmatch@test.com" };
            seedContext.Users.Add(user);
            seedContext.W9Requests.Add(new W9Request
            {
                UserId = 99,
                Email = "tinmatch@test.com",
                SubmissionId = "sub-tinmatch-fail",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        var mockDbFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockDbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(options));

        // Skip signature verification
        _mockConfiguration.Setup(c => c["TaxBandits:ClientId"]).Returns((string)null);
        _mockConfiguration.Setup(c => c["TaxBandits:ClientSecret"]).Returns((string)null);

        // TIN match API returns failure with a raw technical error message
        var rawApiError = "Middle Name is Invalid. The Middle Name can have Alphabets, Numbers and Special Characters ( & - ).";
        _mockTaxBanditsService
            .Setup(s => s.RequestInstantTinMatchAsync(It.IsAny<InstantTinMatchRequest>()))
            .ReturnsAsync(new InstantTinMatchResponse { Success = false, ErrorMessage = rawApiError });

        _mockCreatorService
            .Setup(s => s.GetCreatorByUserIdAsync(99))
            .ReturnsAsync(new Creator { Id = 10, UserId = 99 });
        _mockCreatorService
            .Setup(s => s.UpdateTaxFormStatusAsync(It.IsAny<int>(), It.IsAny<TaxFormStatus>(), It.IsAny<string>()))
            .ReturnsAsync(new Creator { Id = 10, UserId = 99 });

        // Capture the arguments passed to SignalR SendAsync
        WebhookStatusMessage broadcastedMessage = null;
        var mockClientProxy = new Mock<IClientProxy>();
        mockClientProxy
            .Setup(p => p.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (method == "ReceiveWebhookStatus" && args.Length > 0)
                {
                    broadcastedMessage = args[0] as WebhookStatusMessage;
                }
            })
            .Returns(Task.CompletedTask);

        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        var mockHub = new Mock<IHubContext<WebhookStatusHub>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var controller = new TaxBanditsController(
            mockDbFactory.Object,
            _mockUserManager.Object,
            _mockRoleManager.Object,
            _mockCreatorService.Object,
            _mockCreatorEmailService.Object,
            _mockTaxBanditsService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            mockHub.Object,
            _mockAdminNotificationService.Object);

        var webhookBody = @"{
            ""FormType"": ""FORMW9"",
            ""FormW9"": {
                ""SubmissionId"": ""sub-tinmatch-fail"",
                ""PayeeRef"": ""tinmatch@test.com"",
                ""W9Status"": ""COMPLETED"",
                ""RecipientId"": ""rec-99"",
                ""FormData"": {
                    ""TINType"": ""SSN"",
                    ""TIN"": ""987654321"",
                    ""FirstNm"": ""Jane"",
                    ""LastNm"": ""Doe""
                }
            }
        }";

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookBody));
        context.Request.ContentLength = webhookBody.Length;
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        // Act
        var result = await controller.HandleW9CompleteWebhook();

        // Assert — webhook returns success (200 OK with "success" status)
        Assert.That(result, Is.InstanceOf<OkObjectResult>());

        // Assert — the broadcast message is the generic user-safe message, NOT the raw API error
        Assert.That(broadcastedMessage, Is.Not.Null, "A SignalR broadcast should have been sent");
        Assert.That(broadcastedMessage!.Message, Does.Not.Contain(rawApiError),
            "Raw API error text must not be broadcast to the user via SignalR");
        Assert.That(broadcastedMessage.Message, Does.Contain("problem validating your tax form"),
            "Broadcast should use the generic user-safe message");
        Assert.That(broadcastedMessage.NewStatus, Is.EqualTo(nameof(TaxFormStatus.Pending)),
            "Broadcast status should match the TaxFormStatus enum name");
        Assert.That(broadcastedMessage.IsSuccess, Is.False);
    }
}
