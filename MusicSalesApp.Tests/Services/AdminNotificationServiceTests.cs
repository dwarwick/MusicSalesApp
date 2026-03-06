using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.IO;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AdminNotificationServiceTests
{
    private Mock<IEmailService> _mockEmailService;
    private Mock<IAppSettingsService> _mockAppSettingsService;
    private Mock<ISongMetadataService> _mockSongMetadataService;
    private Mock<INewSongNotificationService> _mockNewSongNotificationService;
    private Mock<IAzureStorageService> _mockAzureStorageService;
    private Mock<ILogger<AdminNotificationService>> _mockLogger;
    private IDbContextFactory<AppDbContext> _contextFactory;
    private DbContextOptions<AppDbContext> _dbOptions;
    private AdminNotificationService _service;

    [SetUp]
    public void SetUp()
    {
        _mockEmailService = new Mock<IEmailService>();
        _mockAppSettingsService = new Mock<IAppSettingsService>();
        _mockSongMetadataService = new Mock<ISongMetadataService>();
        _mockNewSongNotificationService = new Mock<INewSongNotificationService>();
        _mockAzureStorageService = new Mock<IAzureStorageService>();
        _mockLogger = new Mock<ILogger<AdminNotificationService>>();

        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockEmailService.Setup(x => x.GetLogoUrl())
            .Returns("https://streamtunes.net/images/logo-light-small.png");
        _mockEmailService.Setup(x => x.GetAppBaseUrl())
            .Returns("https://streamtunes.net");
        _mockNewSongNotificationService.Setup(x => x.BuildSongListHtml(It.IsAny<List<SongMetadata>>(), It.IsAny<string>()))
            .Returns<List<SongMetadata>, string>((songs, title) =>
            {
                // Return a simple HTML representation that includes song titles for assertions
                var songHtml = string.Join("", songs
                    .Where(s => !s.IsAlbumCover && !string.IsNullOrEmpty(s.Mp3BlobPath))
                    .Select(s => $"<tr><td>{System.Web.HttpUtility.HtmlEncode(s.SongTitle ?? Path.GetFileNameWithoutExtension(s.Mp3BlobPath))}</td></tr>"));
                return $"<h2>{title}</h2><table>{songHtml}</table>";
            });

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _contextFactory = new TestDbContextFactory(_dbOptions);

        // Seed a test user
        using var context = new AppDbContext(_dbOptions);
        context.Users.Add(new ApplicationUser
        {
            Id = 1,
            UserName = "test@example.com",
            NormalizedUserName = "TEST@EXAMPLE.COM",
            Email = "test@example.com",
            NormalizedEmail = "TEST@EXAMPLE.COM",
            EmailConfirmed = true
        });
        context.SaveChanges();

        _service = new AdminNotificationService(
            _mockEmailService.Object,
            _mockAppSettingsService.Object,
            _contextFactory,
            _mockSongMetadataService.Object,
            _mockNewSongNotificationService.Object,
            _mockAzureStorageService.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task NotifyUserRegisteredAsync_SendsEmailAndRecordsHistory()
    {
        // Arrange - notifications enabled by default (null setting)
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifyRegistrationKey))
            .ReturnsAsync((string)null);

        // Act
        await _service.NotifyUserRegisteredAsync("test@example.com");

        // Assert - email sent to admin
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Registration")),
                It.Is<string>(body => body.Contains("test@example.com"))),
            Times.Once);

        // Assert - history recorded
        using var context = new AppDbContext(_dbOptions);
        var history = await context.UserHistories.FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.EventType, Is.EqualTo("Registration"));
        Assert.That(history.UserEmail, Is.EqualTo("test@example.com"));
    }

    [Test]
    public async Task NotifyUserRegisteredAsync_DoesNotSendEmail_WhenDisabled()
    {
        // Arrange - notifications disabled
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifyRegistrationKey))
            .ReturnsAsync("False");

        // Act
        await _service.NotifyUserRegisteredAsync("test@example.com");

        // Assert - no email sent
        _mockEmailService.Verify(
            x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);

        // Assert - history is still recorded even when email is disabled
        using var context = new AppDbContext(_dbOptions);
        var history = await context.UserHistories.FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.EventType, Is.EqualTo("Registration"));
    }

    [Test]
    public async Task NotifyEmailConfirmedAsync_SendsEmailAndRecordsHistory()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifyEmailConfirmedKey))
            .ReturnsAsync((string)null);

        // Act
        await _service.NotifyEmailConfirmedAsync("test@example.com");

        // Assert
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Email Confirmed")),
                It.Is<string>(body => body.Contains("test@example.com"))),
            Times.Once);

        using var context = new AppDbContext(_dbOptions);
        var history = await context.UserHistories.FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.EventType, Is.EqualTo("EmailConfirmed"));
    }

    [Test]
    public async Task NotifyTaxFormCompletedAsync_IncludesFormType()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifyTaxFormCompletedKey))
            .ReturnsAsync((string)null);

        // Act
        await _service.NotifyTaxFormCompletedAsync("test@example.com", "W-9");

        // Assert
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("W-9")),
                It.Is<string>(body => body.Contains("test@example.com") && body.Contains("W-9"))),
            Times.Once);

        using var context = new AppDbContext(_dbOptions);
        var history = await context.UserHistories.FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.EventType, Is.EqualTo("TaxFormCompleted"));
        Assert.That(history.Description, Does.Contain("W-9"));
    }

    [Test]
    public async Task NotifyCreatorStatusGainedAsync_RecordsOldAndNewValues()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifyCreatorStatusGainedKey))
            .ReturnsAsync((string)null);

        // Act
        await _service.NotifyCreatorStatusGainedAsync("test@example.com");

        // Assert
        using var context = new AppDbContext(_dbOptions);
        var history = await context.UserHistories.FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.EventType, Is.EqualTo("CreatorStatusGained"));
        Assert.That(history.OldValue, Is.EqualTo("Non-Creator"));
        Assert.That(history.NewValue, Is.EqualTo("Creator"));
    }

    [Test]
    public async Task NotifyCreatorStatusLostAsync_RecordsOldAndNewValues()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifyCreatorStatusLostKey))
            .ReturnsAsync((string)null);

        // Act
        await _service.NotifyCreatorStatusLostAsync("test@example.com");

        // Assert
        using var context = new AppDbContext(_dbOptions);
        var history = await context.UserHistories.FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.EventType, Is.EqualTo("CreatorStatusLost"));
        Assert.That(history.OldValue, Is.EqualTo("Creator"));
        Assert.That(history.NewValue, Is.EqualTo("Non-Creator"));
    }

    [Test]
    public async Task NotifyUploadBatchCompletedAsync_SendsSummaryEmailToAdminAndCreator()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifyUploadCompletedKey))
            .ReturnsAsync((string)null);

        var uploadedSongs = new List<SongMetadata>
        {
            new SongMetadata { Id = 1, CreatorId = 42, Mp3BlobPath = "song1/song1.mp3", ImageBlobPath = "song1/song1.jpg", SongTitle = "My Song", CreatedAt = DateTime.UtcNow },
            new SongMetadata { Id = 2, CreatorId = 42, Mp3BlobPath = "song2/song2.mp3", ImageBlobPath = "song2/song2.jpg", SongTitle = "My Other Song", CreatedAt = DateTime.UtcNow }
        };
        _mockSongMetadataService.Setup(x => x.GetByCreatorIdAsync(42)).ReturnsAsync(uploadedSongs);

        // Act — pass the actual MP3 blob paths (same as in database)
        await _service.NotifyUploadBatchCompletedAsync("test@example.com", 42, new List<string> { "song1/song1.mp3", "song2/song2.mp3" });

        // Assert - admin email sent with summary
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Upload") && s.Contains("test@example.com")),
                It.Is<string>(body => body.Contains("test@example.com") && body.Contains("My Song") && body.Contains("My Other Song"))),
            Times.Once);

        // Assert - creator confirmation email sent
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                "test@example.com",
                It.Is<string>(s => s.Contains("Uploaded")),
                It.Is<string>(body => body.Contains("Manage My Songs") && body.Contains("My Song"))),
            Times.Once);

        // Assert - history recorded
        using var context = new AppDbContext(_dbOptions);
        var history = await context.UserHistories.FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.EventType, Is.EqualTo("UploadCompleted"));
        Assert.That(history.Description, Does.Contain("2 file(s)"));
    }

    [Test]
    public async Task NotifySongRenamedAsync_RecordsOldAndNewTitle()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifySongRenamedKey))
            .ReturnsAsync((string)null);

        // Act
        await _service.NotifySongRenamedAsync("test@example.com", "Old Song", "New Song");

        // Assert
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(s => s.Contains("Renamed")),
                It.Is<string>(body => body.Contains("Old Song") && body.Contains("New Song"))),
            Times.Once);

        using var context = new AppDbContext(_dbOptions);
        var history = await context.UserHistories.FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.EventType, Is.EqualTo("SongRenamed"));
        Assert.That(history.OldValue, Is.EqualTo("Old Song"));
        Assert.That(history.NewValue, Is.EqualTo("New Song"));
    }

    [Test]
    public async Task NotifySongArtUpdatedAsync_RecordsEvent()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifySongArtUpdatedKey))
            .ReturnsAsync((string)null);

        // Act
        await _service.NotifySongArtUpdatedAsync("test@example.com", "My Song");

        // Assert
        using var context = new AppDbContext(_dbOptions);
        var history = await context.UserHistories.FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.EventType, Is.EqualTo("SongArtUpdated"));
    }

    [Test]
    public async Task NotifySongArtUpdatedAsync_EmailIncludesSongArtImage()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(AdminNotificationService.NotifySongArtUpdatedKey))
            .ReturnsAsync((string)null);

        // Seed a creator and song with image
        using (var seedContext = new AppDbContext(_dbOptions))
        {
            var creator = new Creator { Id = 1, UserId = 1, IsActive = true, DisplayName = "Test Creator" };
            seedContext.Creators.Add(creator);

            seedContext.SongMetadata.Add(new SongMetadata
            {
                Id = 1,
                CreatorId = 1,
                SongTitle = "My Song",
                Mp3BlobPath = "music/my-song.mp3",
                ImageBlobPath = "music/my-song.jpg",
                IsActive = true,
                IsEnabled = true
            });
            await seedContext.SaveChangesAsync();
        }

        _mockAzureStorageService.Setup(x => x.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://storage.blob.core.windows.net/music/my-song.jpg?sv=2021-01-01&se=2026-03-13"));

        string capturedBody = null!;
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string>((to, subject, body) => capturedBody = body)
            .ReturnsAsync(true);

        // Act
        await _service.NotifySongArtUpdatedAsync("test@example.com", "My Song");

        // Assert - email was sent with the image
        Assert.That(capturedBody, Is.Not.Null);
        Assert.That(capturedBody, Does.Contain("Updated Cover Art"));
        Assert.That(capturedBody, Does.Contain("storage.blob.core.windows.net/music/my-song.jpg"));
        Assert.That(capturedBody, Does.Contain("<img"));
    }

    [Test]
    public async Task IsNotificationEnabledAsync_ReturnsTrue_WhenNotSet()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string)null);

        // Act
        var result = await _service.IsNotificationEnabledAsync("AnyKey");

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsNotificationEnabledAsync_ReturnsFalse_WhenSetToFalse()
    {
        // Arrange
        _mockAppSettingsService.Setup(x => x.GetSettingAsync("TestKey"))
            .ReturnsAsync("False");

        // Act
        var result = await _service.IsNotificationEnabledAsync("TestKey");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task SetNotificationEnabledAsync_CallsAppSettingsService()
    {
        // Act
        await _service.SetNotificationEnabledAsync("TestKey", true);

        // Assert
        _mockAppSettingsService.Verify(
            x => x.SetSettingAsync("TestKey", "True", It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task GetAllUserHistoryAsync_ReturnsAllRecords()
    {
        // Arrange - add some history records
        using (var context = new AppDbContext(_dbOptions))
        {
            context.UserHistories.Add(new UserHistory
            {
                UserId = 1,
                UserEmail = "test@example.com",
                EventType = "Registration",
                Description = "Test registration",
                OccurredAt = DateTime.UtcNow.AddHours(-1)
            });
            context.UserHistories.Add(new UserHistory
            {
                UserId = 1,
                UserEmail = "test@example.com",
                EventType = "EmailConfirmed",
                Description = "Test email confirmed",
                OccurredAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _service.GetAllUserHistoryAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].EventType, Is.EqualTo("EmailConfirmed")); // Most recent first
        Assert.That(result[1].EventType, Is.EqualTo("Registration"));
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
