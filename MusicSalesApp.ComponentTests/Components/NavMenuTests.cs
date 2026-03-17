using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Helpers;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class NavMenuTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        MockMaintenanceHubClient.Setup(x => x.StartAsync()).Returns(Task.CompletedTask);
    }

    [Test]
    public void NavMenu_ShowsBannerAndDialog_WhenMaintenanceActiveAndNotAcknowledged()
    {
        // Arrange
        SetupRendererInfo();

        MockAppSettingsService
            .Setup(x => x.ShouldShowSiteMaintenanceNoticeAsync())
            .ReturnsAsync(true);
        MockAppSettingsService
            .Setup(x => x.GetSiteMaintenanceStartUtcAsync())
            .ReturnsAsync(DateTime.UtcNow.AddHours(1));
        MockAppSettingsService
            .Setup(x => x.GetSiteMaintenanceEndUtcAsync())
            .ReturnsAsync(DateTime.UtcNow.AddHours(3));

        // getMaintenanceLocalTime is called with arguments (startUtc, endUtc); use a wildcard
        // matcher (_ => true) because the default Setup(identifier) overload only matches
        // zero-argument calls in bUnit, and Loose mode would return null for reference types.
        TestContext.JSInterop
            .Setup<MaintenanceLocalTimeInfo>("getMaintenanceLocalTime", _ => true)
            .SetResult(new MaintenanceLocalTimeInfo
            {
                StartLocal = "9:00 AM",
                EndLocal = "11:00 AM",
                TimeZoneAbbreviation = "PT"
            });

        // checkMaintenanceAcknowledged – Loose mode returns false (bool default) for unregistered
        // calls, but we register it here for clarity (not acknowledged).
        TestContext.JSInterop
            .Setup<bool>("checkMaintenanceAcknowledged", _ => true)
            .SetResult(false);

        // Act
        var cut = TestContext.Render<NavMenu>();

        // Wait for OnAfterRenderAsync data loading to complete
        cut.WaitForState(() => cut.Markup.Contains("maintenance-banner"), timeout: TimeSpan.FromSeconds(5));

        // Assert – maintenance banner is shown
        Assert.That(cut.Markup, Does.Contain("maintenance-banner"));
        Assert.That(cut.Markup, Does.Contain("Planned maintenance"));
        // Assert – dialog is shown because user has not acknowledged it yet
        Assert.That(cut.Markup, Does.Contain("Planned Maintenance Notice"));
    }

    [Test]
    public void NavMenu_ShowsBannerButHidesDialog_WhenMaintenanceActiveAndAlreadyAcknowledged()
    {
        // Arrange
        SetupRendererInfo();

        MockAppSettingsService
            .Setup(x => x.ShouldShowSiteMaintenanceNoticeAsync())
            .ReturnsAsync(true);
        MockAppSettingsService
            .Setup(x => x.GetSiteMaintenanceStartUtcAsync())
            .ReturnsAsync(DateTime.UtcNow.AddHours(1));
        MockAppSettingsService
            .Setup(x => x.GetSiteMaintenanceEndUtcAsync())
            .ReturnsAsync(DateTime.UtcNow.AddHours(3));

        TestContext.JSInterop
            .Setup<MaintenanceLocalTimeInfo>("getMaintenanceLocalTime", _ => true)
            .SetResult(new MaintenanceLocalTimeInfo
            {
                StartLocal = "9:00 AM",
                EndLocal = "11:00 AM",
                TimeZoneAbbreviation = "PT"
            });

        // Return true to simulate an already-acknowledged maintenance window.
        TestContext.JSInterop
            .Setup<bool>("checkMaintenanceAcknowledged", _ => true)
            .SetResult(true);

        // Act
        var cut = TestContext.Render<NavMenu>();

        // Wait for OnAfterRenderAsync data loading to complete
        cut.WaitForState(() => cut.Markup.Contains("maintenance-banner"), timeout: TimeSpan.FromSeconds(5));

        // Assert – banner is still visible
        Assert.That(cut.Markup, Does.Contain("maintenance-banner"));
        // Assert – dialog is hidden because it was already acknowledged
        Assert.That(cut.Markup, Does.Not.Contain("maintenance-notice-dialog"));
    }

    [Test]
    public async Task NavMenu_HidesBannerAndDialog_WhenNoMaintenanceScheduled()
    {
        // Arrange
        MockAppSettingsService
            .Setup(x => x.ShouldShowSiteMaintenanceNoticeAsync())
            .ReturnsAsync(false);

        // Act
        var cut = TestContext.Render<NavMenu>();
        await cut.InvokeAsync(() => { });

        // Assert – neither the banner nor the dialog is rendered
        Assert.That(cut.Markup, Does.Not.Contain("maintenance-banner"));
        Assert.That(cut.Markup, Does.Not.Contain("maintenance-notice-dialog"));
    }
}
