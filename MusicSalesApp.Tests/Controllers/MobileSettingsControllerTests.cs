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
        appSettingsService.Setup(service => service.GetStreamQualifyingSettingsAsync())
            .ReturnsAsync(new StreamQualifyingSettings(45, ReductionEnabled: false));
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

    [Test]
    public async Task GetMobileSettings_AppliesThePromotionalReductionServerSide()
    {
        // The reduction is applied here rather than in the app, so turning the flag on does not need a
        // store release. Nothing on the mobile side re-applies it, so if this stops reducing, the
        // feature silently stops working for every phone.
        var appSettingsService = new Mock<IAppSettingsService>();
        appSettingsService.Setup(service => service.GetStreamQualifyingSettingsAsync())
            .ReturnsAsync(new StreamQualifyingSettings(65, ReductionEnabled: true));
        var controller = new MobileSettingsController(appSettingsService.Object);

        var result = await controller.GetMobileSettings();

        var json = JsonSerializer.Serialize(((OkObjectResult)result).Value);
        var expected = 65 - StreamQualifyingPolicy.PromotionalReductionSeconds;
        Assert.That(json, Does.Contain($"\"streamQualifyingSeconds\":{expected}"));
    }
}
