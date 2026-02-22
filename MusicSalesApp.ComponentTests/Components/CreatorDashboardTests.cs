using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class CreatorDashboardTests : BUnitTestBase
{
    private void SetupAuthenticatedCreator(int userId = 1, int creatorId = 10)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "creator@test.com"),
            new Claim(ClaimTypes.Email, "creator@test.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);

        MockAuthStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        MockUserManager
            .Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(new ApplicationUser { Id = userId, Email = "creator@test.com" });

        MockCreatorService
            .Setup(x => x.GetCreatorIdForUserAsync(userId))
            .ReturnsAsync(creatorId);

        var auth = TestContext.AddAuthorization();
        auth.SetAuthorized("creator@test.com");
        auth.SetPolicies("ManageOwnSongs");
    }

    [Test]
    public void CreatorDashboard_ShowsPageTitle()
    {
        // Arrange
        SetupAuthenticatedCreator();

        // Act
        var cut = TestContext.Render<CreatorDashboard>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Creator Dashboard"));
    }

    [Test]
    public void CreatorDashboard_ShowsAnalyticsSubtitle()
    {
        // Arrange
        SetupAuthenticatedCreator();

        // Act
        var cut = TestContext.Render<CreatorDashboard>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("View analytics for your music"));
    }

    [Test]
    public async Task CreatorDashboard_ShowsNoDataMessage_WhenNoStreams()
    {
        // Arrange
        SetupAuthenticatedCreator();

        MockDashboardService
            .Setup(x => x.GetStreamDataAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<StreamInterval>()))
            .ReturnsAsync(new List<StreamDataPoint>());

        // Act
        var cut = TestContext.Render<CreatorDashboard>();

        // Wait for async loading
        await Task.Delay(100);
        cut.Render();

        // Assert
        Assert.That(cut.Markup, Does.Contain("No stream data available for the selected period."));
    }

    [Test]
    public async Task CreatorDashboard_ShowsChart_WhenDataAvailable()
    {
        // Arrange
        SetupAuthenticatedCreator();

        var testData = new List<StreamDataPoint>
        {
            new StreamDataPoint { PeriodStart = DateTime.UtcNow.AddDays(-2), StreamCount = 5 },
            new StreamDataPoint { PeriodStart = DateTime.UtcNow.AddDays(-1), StreamCount = 10 },
            new StreamDataPoint { PeriodStart = DateTime.UtcNow, StreamCount = 3 }
        };

        MockDashboardService
            .Setup(x => x.GetStreamDataAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<StreamInterval>()))
            .ReturnsAsync(testData);

        // Act
        var cut = TestContext.Render<CreatorDashboard>();

        // Wait for async loading
        await Task.Delay(100);
        cut.Render();

        // Assert - Chart component should be rendered (SfChart renders as e-chart)
        Assert.That(cut.Markup, Does.Contain("Overall Streams"));
    }

    [Test]
    public async Task CreatorDashboard_ShowsControls_WhenLoaded()
    {
        // Arrange
        SetupAuthenticatedCreator();

        MockDashboardService
            .Setup(x => x.GetStreamDataAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<StreamInterval>()))
            .ReturnsAsync(new List<StreamDataPoint>());

        // Act
        var cut = TestContext.Render<CreatorDashboard>();

        // Wait for async loading
        await Task.Delay(100);
        cut.Render();

        // Assert - Controls should be present
        Assert.That(cut.Markup, Does.Contain("Start Date/Time"));
        Assert.That(cut.Markup, Does.Contain("End Date/Time"));
        Assert.That(cut.Markup, Does.Contain("Interval"));
    }

    [Test]
    public async Task CreatorDashboard_ShowsTimezoneNote()
    {
        // Arrange
        SetupAuthenticatedCreator();

        MockDashboardService
            .Setup(x => x.GetStreamDataAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<StreamInterval>()))
            .ReturnsAsync(new List<StreamDataPoint>());

        // Act
        var cut = TestContext.Render<CreatorDashboard>();

        // Wait for async loading
        await Task.Delay(100);
        cut.Render();

        // Assert - should show timezone info (detected from browser or UTC fallback)
        Assert.That(cut.Markup, Does.Contain("time zone"));
    }

    [Test]
    public async Task CreatorDashboard_CallsDashboardService_WithCorrectCreatorId()
    {
        // Arrange
        int expectedCreatorId = 10;
        SetupAuthenticatedCreator(creatorId: expectedCreatorId);

        MockDashboardService
            .Setup(x => x.GetStreamDataAsync(expectedCreatorId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<StreamInterval>()))
            .ReturnsAsync(new List<StreamDataPoint>());

        // Act
        var cut = TestContext.Render<CreatorDashboard>();

        // Wait for async loading
        await Task.Delay(100);

        // Assert
        MockDashboardService.Verify(
            x => x.GetStreamDataAsync(expectedCreatorId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), StreamInterval.Day),
            Times.Once);
    }
}
