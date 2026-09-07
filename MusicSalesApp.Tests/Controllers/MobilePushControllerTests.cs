using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class MobilePushControllerTests
{
    private const int TestUserId = 100;

    private Mock<IPushDeviceTokenService> _deviceTokenService;
    private MobilePushController _controller;

    [SetUp]
    public void SetUp()
    {
        _deviceTokenService = new Mock<IPushDeviceTokenService>();
        _controller = new MobilePushController(_deviceTokenService.Object);
        SetAuthenticatedUser(TestUserId);
    }

    private void SetAuthenticatedUser(int? userId)
    {
        var identity = userId.HasValue
            ? new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())], "TestAuth")
            : new ClaimsIdentity();

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    [Test]
    public async Task RegisterDevice_RegistersForTheCallingUser()
    {
        _deviceTokenService
            .Setup(x => x.RegisterAsync(
                TestUserId, PushPlatforms.Android, "token-abc", "device-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.RegisterDevice(new RegisterPushDeviceRequest
        {
            Platform = PushPlatforms.Android,
            Token = "token-abc",
            DeviceId = "device-1",
        });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkResult>());
            _deviceTokenService.Verify(
                x => x.RegisterAsync(
                    TestUserId, PushPlatforms.Android, "token-abc", "device-1", It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Test]
    public async Task RegisterDevice_RejectsAnUnknownPlatformBeforeTouchingTheService()
    {
        var result = await _controller.RegisterDevice(new RegisterPushDeviceRequest
        {
            Platform = "Symbian",
            Token = "token-abc",
        });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            _deviceTokenService.Verify(
                x => x.RegisterAsync(
                    It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public async Task RegisterDevice_RejectsAnEmptyToken(string token)
    {
        var result = await _controller.RegisterDevice(new RegisterPushDeviceRequest
        {
            Platform = PushPlatforms.Ios,
            Token = token,
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task RegisterDevice_AnswersARefusalWith400NotAServerError()
    {
        // The client retries a 5xx forever, and an over-long or malformed token never becomes
        // valid. Same contract as the follow routes.
        _deviceTokenService
            .Setup(x => x.RegisterAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.RegisterDevice(new RegisterPushDeviceRequest
        {
            Platform = PushPlatforms.Android,
            Token = "token-abc",
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task RegisterDevice_RequiresAnAuthenticatedUser()
    {
        SetAuthenticatedUser(null);

        var result = await _controller.RegisterDevice(new RegisterPushDeviceRequest
        {
            Platform = PushPlatforms.Android,
            Token = "token-abc",
        });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
            _deviceTokenService.Verify(
                x => x.RegisterAsync(
                    It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        });
    }

    [Test]
    public async Task UnregisterDevice_IsNotAnErrorWhenTheTokenIsAlreadyGone()
    {
        // A client signing out twice, or replaying a queued unregister, has got what it wanted.
        _deviceTokenService
            .Setup(x => x.UnregisterAsync(TestUserId, "token-abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.UnregisterDevice(new UnregisterPushDeviceRequest { Token = "token-abc" });

        Assert.That(result, Is.InstanceOf<OkResult>());
    }

    [Test]
    public async Task UnregisterDevice_PassesTheCallersIdSoOnlyTheirOwnDeviceIsTouched()
    {
        await _controller.UnregisterDevice(new UnregisterPushDeviceRequest { Token = "token-abc" });

        _deviceTokenService.Verify(
            x => x.UnregisterAsync(TestUserId, "token-abc", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void EveryRouteIsGatedByBothTheApiKeyAndAToken()
    {
        var attributes = typeof(MobilePushController).GetCustomAttributes(inherit: true);

        Assert.Multiple(() =>
        {
            Assert.That(
                attributes.Any(a => a.GetType().Name == "RequireMobileApiKeyAttribute"), Is.True);
            Assert.That(
                attributes.Any(a => a.GetType().Name == "AuthorizeAttribute"), Is.True);
        });
    }
}
