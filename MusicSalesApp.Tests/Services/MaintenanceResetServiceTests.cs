using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Hubs;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class MaintenanceResetServiceTests
{
    private Mock<IAppSettingsService> _mockAppSettings;
    private Mock<IHubContext<MaintenanceHub>> _mockHubContext;
    private Mock<ILogger<MaintenanceResetService>> _mockLogger;
    private MaintenanceResetService _service;

    [SetUp]
    public void SetUp()
    {
        _mockAppSettings = new Mock<IAppSettingsService>();
        _mockLogger = new Mock<ILogger<MaintenanceResetService>>();

        _mockHubContext = new Mock<IHubContext<MaintenanceHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockAllClients = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockAllClients.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        _service = new MaintenanceResetService(_mockAppSettings.Object, _mockHubContext.Object, _mockLogger.Object);
    }

    [Test]
    public async Task ResetExpiredMaintenanceWindowsAsync_ResetsSiteWindow_WhenExpired()
    {
        // Arrange - site maintenance ended an hour ago
        var pastEnd = DateTime.UtcNow.AddHours(-1);
        _mockAppSettings.Setup(x => x.GetSiteMaintenanceEndUtcAsync()).ReturnsAsync(pastEnd);
        _mockAppSettings.Setup(x => x.GetTaxBanditsMaintenanceEndUtcAsync()).ReturnsAsync((DateTime?)null);

        // Act
        await _service.ResetExpiredMaintenanceWindowsAsync();

        // Assert
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceStartUtcAsync(DateTime.MinValue), Times.Once);
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceEndUtcAsync(DateTime.MinValue), Times.Once);
    }

    [Test]
    public async Task ResetExpiredMaintenanceWindowsAsync_DoesNotResetSiteWindow_WhenStillActive()
    {
        // Arrange - site maintenance ends in the future
        var futureEnd = DateTime.UtcNow.AddHours(2);
        _mockAppSettings.Setup(x => x.GetSiteMaintenanceEndUtcAsync()).ReturnsAsync(futureEnd);
        _mockAppSettings.Setup(x => x.GetTaxBanditsMaintenanceEndUtcAsync()).ReturnsAsync((DateTime?)null);

        // Act
        await _service.ResetExpiredMaintenanceWindowsAsync();

        // Assert
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceStartUtcAsync(It.IsAny<DateTime>()), Times.Never);
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceEndUtcAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Test]
    public async Task ResetExpiredMaintenanceWindowsAsync_DoesNotResetSiteWindow_WhenEndIsNull()
    {
        // Arrange
        _mockAppSettings.Setup(x => x.GetSiteMaintenanceEndUtcAsync()).ReturnsAsync((DateTime?)null);
        _mockAppSettings.Setup(x => x.GetTaxBanditsMaintenanceEndUtcAsync()).ReturnsAsync((DateTime?)null);

        // Act
        await _service.ResetExpiredMaintenanceWindowsAsync();

        // Assert
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceStartUtcAsync(It.IsAny<DateTime>()), Times.Never);
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceEndUtcAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Test]
    public async Task ResetExpiredMaintenanceWindowsAsync_DoesNotResetSiteWindow_WhenEndIsMinValue()
    {
        // Arrange - already reset
        _mockAppSettings.Setup(x => x.GetSiteMaintenanceEndUtcAsync()).ReturnsAsync(DateTime.MinValue);
        _mockAppSettings.Setup(x => x.GetTaxBanditsMaintenanceEndUtcAsync()).ReturnsAsync((DateTime?)null);

        // Act
        await _service.ResetExpiredMaintenanceWindowsAsync();

        // Assert
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceStartUtcAsync(It.IsAny<DateTime>()), Times.Never);
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceEndUtcAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Test]
    public async Task ResetExpiredMaintenanceWindowsAsync_ResetsTaxBanditsWindow_WhenExpired()
    {
        // Arrange
        var pastEnd = DateTime.UtcNow.AddHours(-1);
        _mockAppSettings.Setup(x => x.GetSiteMaintenanceEndUtcAsync()).ReturnsAsync((DateTime?)null);
        _mockAppSettings.Setup(x => x.GetTaxBanditsMaintenanceEndUtcAsync()).ReturnsAsync(pastEnd);

        // Act
        await _service.ResetExpiredMaintenanceWindowsAsync();

        // Assert
        _mockAppSettings.Verify(x => x.SetTaxBanditsMaintenanceStartUtcAsync(DateTime.MinValue), Times.Once);
        _mockAppSettings.Verify(x => x.SetTaxBanditsMaintenanceEndUtcAsync(DateTime.MinValue), Times.Once);
    }

    [Test]
    public async Task ResetExpiredMaintenanceWindowsAsync_DoesNotResetTaxBanditsWindow_WhenStillActive()
    {
        // Arrange
        var futureEnd = DateTime.UtcNow.AddHours(2);
        _mockAppSettings.Setup(x => x.GetSiteMaintenanceEndUtcAsync()).ReturnsAsync((DateTime?)null);
        _mockAppSettings.Setup(x => x.GetTaxBanditsMaintenanceEndUtcAsync()).ReturnsAsync(futureEnd);

        // Act
        await _service.ResetExpiredMaintenanceWindowsAsync();

        // Assert
        _mockAppSettings.Verify(x => x.SetTaxBanditsMaintenanceStartUtcAsync(It.IsAny<DateTime>()), Times.Never);
        _mockAppSettings.Verify(x => x.SetTaxBanditsMaintenanceEndUtcAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Test]
    public async Task ResetExpiredMaintenanceWindowsAsync_ResetsBothWindows_WhenBothExpired()
    {
        // Arrange
        var pastEnd = DateTime.UtcNow.AddHours(-1);
        _mockAppSettings.Setup(x => x.GetSiteMaintenanceEndUtcAsync()).ReturnsAsync(pastEnd);
        _mockAppSettings.Setup(x => x.GetTaxBanditsMaintenanceEndUtcAsync()).ReturnsAsync(pastEnd);

        // Act
        await _service.ResetExpiredMaintenanceWindowsAsync();

        // Assert - site window reset
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceStartUtcAsync(DateTime.MinValue), Times.Once);
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceEndUtcAsync(DateTime.MinValue), Times.Once);
        // Assert - tax bandits window reset
        _mockAppSettings.Verify(x => x.SetTaxBanditsMaintenanceStartUtcAsync(DateTime.MinValue), Times.Once);
        _mockAppSettings.Verify(x => x.SetTaxBanditsMaintenanceEndUtcAsync(DateTime.MinValue), Times.Once);
    }

    [Test]
    public async Task ResetExpiredMaintenanceWindowsAsync_ResetsOnlySite_WhenOnlySiteExpired()
    {
        // Arrange - site expired, tax bandits still active
        var pastEnd = DateTime.UtcNow.AddHours(-1);
        var futureEnd = DateTime.UtcNow.AddHours(2);
        _mockAppSettings.Setup(x => x.GetSiteMaintenanceEndUtcAsync()).ReturnsAsync(pastEnd);
        _mockAppSettings.Setup(x => x.GetTaxBanditsMaintenanceEndUtcAsync()).ReturnsAsync(futureEnd);

        // Act
        await _service.ResetExpiredMaintenanceWindowsAsync();

        // Assert - site window reset
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceStartUtcAsync(DateTime.MinValue), Times.Once);
        _mockAppSettings.Verify(x => x.SetSiteMaintenanceEndUtcAsync(DateTime.MinValue), Times.Once);
        // Assert - tax bandits NOT reset
        _mockAppSettings.Verify(x => x.SetTaxBanditsMaintenanceStartUtcAsync(It.IsAny<DateTime>()), Times.Never);
    }
}
