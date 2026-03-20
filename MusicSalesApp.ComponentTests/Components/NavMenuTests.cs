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
        MockAppSettingsService
            .Setup(x => x.ShouldShowSiteMaintenanceNoticeAsync())
            .ReturnsAsync(false);
    }

    [Test]
    public void NavMenu_ShowsVersion_WhenGetAppVersionAsyncReturnsNonEmptyString()
    {
        // Arrange
        MockAppSettingsService
            .Setup(x => x.GetAppVersionAsync())
            .ReturnsAsync("1.2.3");

        // Act
        var cut = TestContext.Render<NavMenu>();

        // Wait for OnAfterRenderAsync data loading to complete
        cut.WaitForState(() => cut.Markup.Contains("1.2.3"), timeout: TimeSpan.FromSeconds(5));

        // Assert – version is shown in the footer
        Assert.That(cut.Markup, Does.Contain("version: 1.2.3"));
    }

    [Test]
    public async Task NavMenu_HidesVersion_WhenGetAppVersionAsyncReturnsNull()
    {
        // Arrange
        MockAppSettingsService
            .Setup(x => x.GetAppVersionAsync())
            .ReturnsAsync(default(string));

        // Act
        var cut = TestContext.Render<NavMenu>();
        await cut.InvokeAsync(() => { });

        // Assert – version div is not rendered
        Assert.That(cut.Markup, Does.Not.Contain("version:"));
    }

    [Test]
    public async Task NavMenu_HidesVersion_WhenGetAppVersionAsyncReturnsEmptyString()
    {
        // Arrange
        MockAppSettingsService
            .Setup(x => x.GetAppVersionAsync())
            .ReturnsAsync(string.Empty);

        // Act
        var cut = TestContext.Render<NavMenu>();
        await cut.InvokeAsync(() => { });

        // Assert – version div is not rendered
        Assert.That(cut.Markup, Does.Not.Contain("version:"));
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

    [Test]
    public void NavMenu_ClearsMaintenanceBanner_WhenExpiryTimerFires()
    {
        // Arrange
        SetupRendererInfo();

        var now = FakeTimeProvider.GetUtcNow().UtcDateTime;
        var startUtc = now.AddHours(-1); // window started an hour ago
        var endUtc = now.AddHours(2);    // window ends in 2 hours

        // First call (initial render): show maintenance. Second call (timer-triggered): hide it.
        MockAppSettingsService
            .SetupSequence(x => x.ShouldShowSiteMaintenanceNoticeAsync())
            .ReturnsAsync(true)
            .ReturnsAsync(false);

        MockAppSettingsService
            .Setup(x => x.GetSiteMaintenanceStartUtcAsync())
            .ReturnsAsync(startUtc);
        MockAppSettingsService
            .Setup(x => x.GetSiteMaintenanceEndUtcAsync())
            .ReturnsAsync(endUtc);

        TestContext.JSInterop
            .Setup<MaintenanceLocalTimeInfo>("getMaintenanceLocalTime", _ => true)
            .SetResult(new MaintenanceLocalTimeInfo
            {
                StartLocal = "9:00 AM",
                EndLocal = "11:00 AM",
                TimeZoneAbbreviation = "PT"
            });
        TestContext.JSInterop
            .Setup<bool>("checkMaintenanceAcknowledged", _ => true)
            .SetResult(false);

        var cut = TestContext.Render<NavMenu>();
        cut.WaitForState(() => cut.Markup.Contains("maintenance-banner"), timeout: TimeSpan.FromSeconds(5));
        Assert.That(cut.Markup, Does.Contain("maintenance-banner"), "Precondition: banner visible before timer fires");

        // Act: advance fake time past the window end so the timer fires
        FakeTimeProvider.Advance(TimeSpan.FromHours(3));

        // Assert: the async reload triggered by the timer clears the banner
        cut.WaitForState(() => !cut.Markup.Contains("maintenance-banner"), timeout: TimeSpan.FromSeconds(5));
        Assert.That(cut.Markup, Does.Not.Contain("maintenance-banner"));
    }

    [Test]
    public void NavMenu_ShowsBannerWithoutTimer_WhenWindowEndsMoreThan49DaysAway()
    {
        // System.Threading.Timer has an upper bound of ~49.7 days. If the maintenance window
        // end is beyond that, the component must not throw and must still show the banner.
        SetupRendererInfo();

        MockAppSettingsService
            .Setup(x => x.ShouldShowSiteMaintenanceNoticeAsync())
            .ReturnsAsync(true);
        MockAppSettingsService
            .Setup(x => x.GetSiteMaintenanceStartUtcAsync())
            .ReturnsAsync(FakeTimeProvider.GetUtcNow().UtcDateTime.AddHours(1));
        // 100 days is well beyond the ~49.7-day Timer maximum
        MockAppSettingsService
            .Setup(x => x.GetSiteMaintenanceEndUtcAsync())
            .ReturnsAsync(FakeTimeProvider.GetUtcNow().UtcDateTime.AddDays(100));

        TestContext.JSInterop
            .Setup<MaintenanceLocalTimeInfo>("getMaintenanceLocalTime", _ => true)
            .SetResult(new MaintenanceLocalTimeInfo
            {
                StartLocal = "9:00 AM",
                EndLocal = "11:00 AM",
                TimeZoneAbbreviation = "PT"
            });
        TestContext.JSInterop
            .Setup<bool>("checkMaintenanceAcknowledged", _ => true)
            .SetResult(false);

        // Act – should not throw ArgumentOutOfRangeException
        var cut = TestContext.Render<NavMenu>();
        cut.WaitForState(() => cut.Markup.Contains("maintenance-banner"), timeout: TimeSpan.FromSeconds(5));

        // Banner is still shown even though no client-side expiry timer was scheduled
        Assert.That(cut.Markup, Does.Contain("maintenance-banner"));
    }
}
