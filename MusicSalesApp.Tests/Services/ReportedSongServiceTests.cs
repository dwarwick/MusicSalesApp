#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ReportedSongServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<IEmailService> _mockEmailService;
    private Mock<ILogger<ReportedSongService>> _mockLogger;
    private ReportedSongService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"ReportedSongTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _mockEmailService = new Mock<IEmailService>();
        _mockEmailService.Setup(e => e.GetLogoUrl()).Returns("https://example.com/logo.png");
        _mockEmailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockLogger = new Mock<ILogger<ReportedSongService>>();

        _service = new ReportedSongService(_mockContextFactory.Object, _mockEmailService.Object, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task SeedSongAndUserAsync(int songId = 1, int userId = 10, int creatorUserId = 20)
    {
        var creatorUser = new ApplicationUser { Id = creatorUserId, UserName = "creator", Email = "creator@test.com" };
        var reportingUser = new ApplicationUser { Id = userId, UserName = "reporter", Email = "reporter@test.com" };
        _context.Users.AddRange(creatorUser, reportingUser);

        var creator = new Creator { Id = 1, UserId = creatorUserId, DisplayName = "Test Creator" };
        _context.Creators.Add(creator);

        _context.SongMetadata.Add(new SongMetadata
        {
            Id = songId,
            SongTitle = "Test Song",
            CreatorId = creator.Id,
            IsEnabled = true,
            Mp3BlobPath = "test.mp3"
        });

        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task ReportSongAsync_WithValidData_CreatesReport()
    {
        // Arrange
        await SeedSongAndUserAsync();

        // Act
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Assert
        Assert.That(report, Is.Not.Null);
        Assert.That(report.SongMetadataId, Is.EqualTo(1));
        Assert.That(report.ReportingUserId, Is.EqualTo(10));
        Assert.That(report.Reason, Is.EqualTo(ReportReasonTypes.CopyrightViolation));
        Assert.That(report.ResolutionAccepted, Is.Null);

        // Verify it was persisted
        using var verifyContext = new AppDbContext(_contextOptions);
        var saved = await verifyContext.ReportedSongs.FirstOrDefaultAsync();
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.SongMetadataId, Is.EqualTo(1));
    }

    [Test]
    public async Task ReportSongAsync_SendsThreeEmails()
    {
        // Arrange
        await SeedSongAndUserAsync();

        // Act
        await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Assert: admin + creator + reporter = 3 emails
        _mockEmailService.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(3));
    }

    [Test]
    public void ReportSongAsync_WithInvalidReason_Throws()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.ReportSongAsync(10, 1, "Invalid Reason"));
    }

    [Test]
    public async Task ReportSongAsync_WithNonExistentSong_Throws()
    {
        // Arrange: seed user but no song
        _context.Users.Add(new ApplicationUser { Id = 10, UserName = "reporter", Email = "reporter@test.com" });
        await _context.SaveChangesAsync();

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ReportSongAsync(10, 999, ReportReasonTypes.CopyrightViolation));
    }

    [Test]
    public async Task ReportSongAsync_DuplicateReport_Throws()
    {
        // Arrange
        await SeedSongAndUserAsync();
        await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ReportSongAsync(10, 1, ReportReasonTypes.TermsOfUseViolation));
        Assert.That(ex!.Message, Does.Contain("already reported"));
    }

    [Test]
    public async Task ReportSongAsync_DifferentUsersCanReportSameSong()
    {
        // Arrange
        var user2 = new ApplicationUser { Id = 30, UserName = "reporter2", Email = "reporter2@test.com" };
        await SeedSongAndUserAsync();
        _context.Users.Add(user2);
        await _context.SaveChangesAsync();

        // Act
        var report1 = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        var report2 = await _service.ReportSongAsync(30, 1, ReportReasonTypes.TermsOfUseViolation);

        // Assert
        Assert.That(report1.Id, Is.Not.EqualTo(report2.Id));

        using var verifyContext = new AppDbContext(_contextOptions);
        var count = await verifyContext.ReportedSongs.CountAsync();
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetAllReportsAsync_ReturnsAllReports()
    {
        // Arrange
        await SeedSongAndUserAsync();
        await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Act
        var reports = await _service.GetAllReportsAsync();

        // Assert
        Assert.That(reports, Has.Count.EqualTo(1));
        Assert.That(reports[0].SongMetadata, Is.Not.Null);
        Assert.That(reports[0].ReportingUser, Is.Not.Null);
    }

    [Test]
    public async Task ResolveReportAsync_AcceptReport_SetsFields()
    {
        // Arrange
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Act
        var result = await _service.ResolveReportAsync(report.Id, true);

        // Assert
        Assert.That(result, Is.True);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.True);
        Assert.That(resolved.ResolutionDateTime, Is.Not.Null);
    }

    [Test]
    public async Task ResolveReportAsync_RejectReport_SetsFields()
    {
        // Arrange
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Act
        var result = await _service.ResolveReportAsync(report.Id, false);

        // Assert
        Assert.That(result, Is.True);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.False);
    }

    [Test]
    public async Task ResolveReportAsync_NonExistentReport_ReturnsFalse()
    {
        // Act
        var result = await _service.ResolveReportAsync(999, true);

        // Assert
        Assert.That(result, Is.False);
    }
}
