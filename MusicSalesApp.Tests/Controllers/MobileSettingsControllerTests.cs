using Microsoft.AspNetCore.Mvc;
using Moq;
using MusicSalesApp.Controllers;
using MusicSalesApp.Services;
using System.Text.Json;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class MobileSettingsControllerTests
{
    [Test]
    public async Task GetMobileSettings_ReturnsStreamThresholdWithoutSubscriptionPrice()
    {
        var appSettingsService = new Mock<IAppSettingsService>();
        appSettingsService.Setup(service => service.GetStreamQualifyingSecondsAsync())
            .ReturnsAsync(45);
        var controller = new MobileSettingsController(appSettingsService.Object);

        var result = await controller.GetMobileSettings();

        var okResult = result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var json = JsonSerializer.Serialize(okResult!.Value);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"streamQualifyingSeconds\":45"));
            Assert.That(json, Does.Not.Contain("subscriptionPrice"));
        });
    }
}
