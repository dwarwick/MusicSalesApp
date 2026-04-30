using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class MobileTipControllerTests
{
    private Mock<ITipService> _mockTipService;
    private Mock<IConfiguration> _mockConfiguration;
    private DbContextOptions<AppDbContext> _options;
    private string _databaseName;

    [SetUp]
    public void SetUp()
    {
        _mockTipService = new Mock<ITipService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration.Setup(c => c[AppSettingKeys.MobileTipCallbackUrl]).Returns("streamtunes://tip");

        _databaseName = $"MobileTipControllerTests_{Guid.NewGuid()}";
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
    }

    [Test]
    public void Controller_UsesValidatedUserPolicy()
    {
        var authorizeAttribute = typeof(MobileTipController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.That(authorizeAttribute, Is.Not.Null);
        Assert.That(authorizeAttribute!.Policy, Is.EqualTo(Permissions.ValidatedUser));
    }

    [Test]
    public async Task CreateOrder_PassesCurrentUserIpAndConfiguredCallbackUrl()
    {
        var controller = CreateController(userId: 123, remoteIpAddress: "10.0.0.8");
        _mockTipService
            .Setup(s => s.CreateTipOrderAsync(123, 7, 11, 5.00m, "10.0.0.8", "fingerprint-1", "streamtunes://tip"))
            .ReturnsAsync((true, null, "https://paypal.test/approve"));

        var result = await controller.CreateOrder(new MobileCreateTipRequest
        {
            CreatorId = 7,
            SongMetadataId = 11,
            Amount = 5.00m,
            DeviceFingerprint = "fingerprint-1"
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var response = (OkObjectResult)result;
        var payload = response.Value as MobileTipOperationResponse;
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.Success, Is.True);
        Assert.That(payload.ResultKind, Is.EqualTo(MobileTipResultKinds.RequiresApproval));
        Assert.That(payload.ApprovalUrl, Is.EqualTo("https://paypal.test/approve"));
    }

    [Test]
    public async Task CreateOrder_WhenFraudPrevented_ReturnsFraudResultKind()
    {
        var controller = CreateController(userId: 123);
        _mockTipService
            .Setup(s => s.CreateTipOrderAsync(123, 7, null, 5.00m, null, null, "streamtunes://tip"))
            .ReturnsAsync((false, "Unusual activity detected. Please try again later.", null));

        var result = await controller.CreateOrder(new MobileCreateTipRequest
        {
            CreatorId = 7,
            Amount = 5.00m
        });

        var payload = ((OkObjectResult)result).Value as MobileTipOperationResponse;
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.Success, Is.False);
        Assert.That(payload.ResultKind, Is.EqualTo(MobileTipResultKinds.FraudPrevented));
    }

    [Test]
    public async Task Capture_WhenTipBelongsToDifferentUser_ReturnsNotFound()
    {
        await SeedPendingTipAsync(tipperUserId: 999, payPalOrderId: "ORDER-1");
        var controller = CreateController(userId: 123);

        var result = await controller.Capture(new MobileTipOrderRequest { PayPalOrderId = "ORDER-1" });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockTipService.Verify(s => s.CaptureTipAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Capture_WhenRevalidationBlocksTip_ReturnsFraudResultKind()
    {
        await SeedPendingTipAsync(tipperUserId: 123, payPalOrderId: "ORDER-2");
        var controller = CreateController(userId: 123);
        _mockTipService
            .Setup(s => s.CaptureTipAsync("ORDER-2"))
            .ReturnsAsync((false, "Unusual activity detected. Please try again later.", 0m));

        var result = await controller.Capture(new MobileTipOrderRequest { PayPalOrderId = "ORDER-2" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var payload = ((OkObjectResult)result).Value as MobileTipOperationResponse;
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.Success, Is.False);
        Assert.That(payload.ResultKind, Is.EqualTo(MobileTipResultKinds.FraudPrevented));
    }

    [Test]
    public async Task Cancel_WhenTipBelongsToDifferentUser_ReturnsNotFound()
    {
        await SeedPendingTipAsync(tipperUserId: 999, payPalOrderId: "ORDER-3");
        var controller = CreateController(userId: 123);

        var result = await controller.Cancel(new MobileTipOrderRequest { PayPalOrderId = "ORDER-3" });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockTipService.Verify(s => s.CancelTipAsync(It.IsAny<string>()), Times.Never);
    }

    private MobileTipController CreateController(int userId, string remoteIpAddress = null)
    {
        var controller = new MobileTipController(
            _mockTipService.Object,
            _mockConfiguration.Object,
            new TestDbContextFactory(_options));

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(CustomClaimTypes.Permission, Permissions.ValidatedUser)
        ], "Bearer"));

        if (!string.IsNullOrWhiteSpace(remoteIpAddress))
        {
            httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIpAddress);
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }

    private async Task SeedPendingTipAsync(int tipperUserId, string payPalOrderId)
    {
        await using var context = new AppDbContext(_options);
        context.Tips.Add(new Tip
        {
            TipperUserId = tipperUserId,
            CreatorId = 7,
            Amount = 5.00m,
            Status = TipStatus.Pending,
            PayPalOrderId = payPalOrderId,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppDbContext(_options));
        }
    }
}