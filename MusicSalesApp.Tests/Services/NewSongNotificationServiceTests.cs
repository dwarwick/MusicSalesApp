using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class NewSongNotificationServiceTests
{
    private Mock<IEmailService> _mockEmailService;
    private Mock<ISongMetadataService> _mockSongMetadataService;
    private Mock<IAzureStorageService> _mockAzureStorageService;
    private Mock<IDbContextFactory<AppDbContext>> _mockDbContextFactory;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<NewSongNotificationService>> _mockLogger;
    private NewSongNotificationService _service;
    private AppDbContext _dbContext;

    [SetUp]
    public void SetUp()
    {
        _mockEmailService = new Mock<IEmailService>();
        _mockSongMetadataService = new Mock<ISongMetadataService>();
        _mockAzureStorageService = new Mock<IAzureStorageService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<NewSongNotificationService>>();

        // Setup in-memory database
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        _dbContext = new AppDbContext(options);

        _mockDbContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_dbContext);

        // Default configuration
        _mockConfiguration.Setup(x => x["App:BaseUrl"]).Returns("https://streamtunes.net");

        // Default email service setup
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Default Azure storage setup
        _mockAzureStorageService.Setup(x => x.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://storage.blob.core.windows.net/container/image.jpg?sv=2021-06-08"));

        _service = new NewSongNotificationService(
            _mockEmailService.Object,
            _mockSongMetadataService.Object,
            _mockAzureStorageService.Object,
            _mockDbContextFactory.Object,
            _mockConfiguration.Object,
            _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext?.Dispose();
    }

    [Test]
    public async Task SendNewSongNotificationsAsync_WithNoNewSongs_DoesNotSendEmails()
    {
        // Arrange
        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<SongMetadata>());

        // Act
        await _service.SendNewSongNotificationsAsync();

        // Assert
        _mockEmailService.Verify(x => x.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task SendNewSongNotificationsAsync_WithNoOptedInUsers_DoesNotSendEmails()
    {
        // Arrange
        var newSongs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/test.mp3",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(newSongs);

        // No users in database (or none opted in)

        // Act
        await _service.SendNewSongNotificationsAsync();

        // Assert
        _mockEmailService.Verify(x => x.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task SendNewSongNotificationsAsync_WithOptedInUsers_SendsEmails()
    {
        // Arrange
        var newSongs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/test.mp3",
                CreatedAt = DateTime.UtcNow.AddHours(-1),
                Genre = "Rock"
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(newSongs);

        // Add opted-in user
        _dbContext.Users.Add(new ApplicationUser
        {
            Id = 100,
            Email = "opteduser@example.com",
            UserName = "OptedUser",
            EmailConfirmed = true,
            ReceiveNewSongEmails = true,
            IsSuspended = false
        });
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.SendNewSongNotificationsAsync();

        // Assert
        _mockEmailService.Verify(x => x.SendEmailAsync(
            "opteduser@example.com",
            It.Is<string>(s => s.Contains("New")),
            It.Is<string>(body => body.Contains("StreamTunes"))),
            Times.Once);
    }

    [Test]
    public async Task SendNewSongNotificationsAsync_DoesNotSendToUnconfirmedEmails()
    {
        // Arrange
        var newSongs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/test.mp3",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(newSongs);

        // Add user with unconfirmed email
        _dbContext.Users.Add(new ApplicationUser
        {
            Id = 101,
            Email = "unconfirmed@example.com",
            UserName = "Unconfirmed",
            EmailConfirmed = false, // Not confirmed
            ReceiveNewSongEmails = true,
            IsSuspended = false
        });
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.SendNewSongNotificationsAsync();

        // Assert
        _mockEmailService.Verify(x => x.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task SendNewSongNotificationsAsync_DoesNotSendToSuspendedUsers()
    {
        // Arrange
        var newSongs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/test.mp3",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(newSongs);

        // Add suspended user
        _dbContext.Users.Add(new ApplicationUser
        {
            Id = 102,
            Email = "suspended@example.com",
            UserName = "Suspended",
            EmailConfirmed = true,
            ReceiveNewSongEmails = true,
            IsSuspended = true // Suspended
        });
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.SendNewSongNotificationsAsync();

        // Assert
        _mockEmailService.Verify(x => x.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task SendNewSongNotificationsAsync_DoesNotSendToUsersWhoOptedOut()
    {
        // Arrange
        var newSongs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/test.mp3",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(newSongs);

        // Add user who opted out
        _dbContext.Users.Add(new ApplicationUser
        {
            Id = 103,
            Email = "optedout@example.com",
            UserName = "OptedOut",
            EmailConfirmed = true,
            ReceiveNewSongEmails = false, // Opted out
            IsSuspended = false
        });
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.SendNewSongNotificationsAsync();

        // Assert
        _mockEmailService.Verify(x => x.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task SendNewSongNotificationsAsync_EmailIncludesLogo()
    {
        // Arrange
        var newSongs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/test.mp3",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(newSongs);

        _dbContext.Users.Add(new ApplicationUser
        {
            Id = 104,
            Email = "test@example.com",
            UserName = "Test",
            EmailConfirmed = true,
            ReceiveNewSongEmails = true,
            IsSuspended = false
        });
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.SendNewSongNotificationsAsync();

        // Assert - Email body should include logo
        _mockEmailService.Verify(x => x.SendEmailAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("logo-light-small.png"))),
            Times.Once);
    }

    [Test]
    public async Task SendNewSongNotificationsAsync_EmailIncludesManageAccountLink()
    {
        // Arrange
        var newSongs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/test.mp3",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(newSongs);

        _dbContext.Users.Add(new ApplicationUser
        {
            Id = 105,
            Email = "test@example.com",
            UserName = "Test",
            EmailConfirmed = true,
            ReceiveNewSongEmails = true,
            IsSuspended = false
        });
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.SendNewSongNotificationsAsync();

        // Assert - Email body should include manage account link
        _mockEmailService.Verify(x => x.SendEmailAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("manage-account") && body.Contains("email preferences"))),
            Times.Once);
    }

    [Test]
    public async Task GetNewSongsAsync_ReturnsOnlyRecentSongs()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var allSongs = new List<SongMetadata>
        {
            new SongMetadata { Id = 1, Mp3BlobPath = "song1.mp3", CreatedAt = now.AddHours(-12) }, // Recent
            new SongMetadata { Id = 2, Mp3BlobPath = "song2.mp3", CreatedAt = now.AddHours(-30) }, // Old
            new SongMetadata { Id = 3, Mp3BlobPath = "song3.mp3", CreatedAt = now.AddHours(-6) },  // Recent
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(allSongs);

        // Act
        var result = await _service.GetNewSongsAsync(now.AddHours(-24));

        // Assert
        Assert.That(result, Has.Count.EqualTo(2)); // Only songs from past 24 hours
        Assert.That(result.Any(s => s.Id == 1), Is.True);
        Assert.That(result.Any(s => s.Id == 3), Is.True);
        Assert.That(result.Any(s => s.Id == 2), Is.False);
    }

    [Test]
    public async Task SendNewSongNotificationsAsync_WithAlbum_IncludesAlbumInEmail()
    {
        // Arrange
        var newSongs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                AlbumName = "Test Album",
                IsAlbumCover = true,
                ImageBlobPath = "albums/cover.jpg",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            },
            new SongMetadata
            {
                Id = 2,
                AlbumName = "Test Album",
                Mp3BlobPath = "albums/track1.mp3",
                TrackNumber = 1,
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(newSongs);

        _dbContext.Users.Add(new ApplicationUser
        {
            Id = 106,
            Email = "test@example.com",
            UserName = "Test",
            EmailConfirmed = true,
            ReceiveNewSongEmails = true,
            IsSuspended = false
        });
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.SendNewSongNotificationsAsync();

        // Assert - Email body should include album name
        _mockEmailService.Verify(x => x.SendEmailAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("Test Album") && body.Contains("New Albums"))),
            Times.Once);
    }
}
