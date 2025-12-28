using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class PurchaseEmailServiceTests
{
    private Mock<IEmailService> _mockEmailService;
    private Mock<IAzureStorageService> _mockAzureStorageService;
    private Mock<ILogger<PurchaseEmailService>> _mockLogger;
    private Mock<IConfiguration> _mockConfiguration;
    private PurchaseEmailService _service;

    [SetUp]
    public void SetUp()
    {
        _mockEmailService = new Mock<IEmailService>();
        _mockAzureStorageService = new Mock<IAzureStorageService>();
        _mockLogger = new Mock<ILogger<PurchaseEmailService>>();
        _mockConfiguration = new Mock<IConfiguration>();

        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockAzureStorageService.Setup(x => x.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://storage.blob.core.windows.net/container/image.jpg?sv=2021-06-08"));

        _service = new PurchaseEmailService(
            _mockEmailService.Object,
            _mockAzureStorageService.Object,
            _mockLogger.Object,
            _mockConfiguration.Object);
    }

    [Test]
    public async Task SendSongPurchaseConfirmationAsync_WithStandaloneSongs_SendsEmail()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var streamTunesOrderId = "ORD-12345";
        var payPalOrderId = "PP-67890";
        var baseUrl = "https://streamtunes.net";
        var totalAmount = 2.97m;

        var purchasedItems = new List<CartItemWithMetadata>
        {
            new CartItemWithMetadata
            {
                SongFileName = "song1.mp3",
                Price = 0.99m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "songs/song1.mp3",
                    ImageBlobPath = "songs/song1.jpg",
                    SongPrice = 0.99m
                }
            },
            new CartItemWithMetadata
            {
                SongFileName = "song2.mp3",
                Price = 0.99m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "songs/song2.mp3",
                    ImageBlobPath = "songs/song2.jpg",
                    SongPrice = 0.99m
                }
            },
            new CartItemWithMetadata
            {
                SongFileName = "song3.mp3",
                Price = 0.99m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "songs/song3.mp3",
                    ImageBlobPath = "songs/song3.jpg",
                    SongPrice = 0.99m
                }
            }
        };

        // Act
        var result = await _service.SendSongPurchaseConfirmationAsync(
            userEmail,
            userName,
            streamTunesOrderId,
            payPalOrderId,
            purchasedItems,
            totalAmount,
            baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Purchase Confirmation")),
                It.Is<string>(body =>
                    body.Contains(streamTunesOrderId) &&
                    body.Contains(payPalOrderId) &&
                    body.Contains("$2.97") &&
                    body.Contains("Individual Songs"))),
            Times.Once);
    }

    [Test]
    public async Task SendSongPurchaseConfirmationAsync_WithAlbumTracks_GroupsByAlbum()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var streamTunesOrderId = "ORD-12345";
        var payPalOrderId = "PP-67890";
        var baseUrl = "https://streamtunes.net";
        var totalAmount = 9.99m;

        var purchasedItems = new List<CartItemWithMetadata>
        {
            new CartItemWithMetadata
            {
                SongFileName = "album/track1.mp3",
                Price = 3.33m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "album/track1.mp3",
                    ImageBlobPath = "album/track1.jpg",
                    AlbumName = "My Album",
                    TrackNumber = 1,
                    SongPrice = 3.33m
                }
            },
            new CartItemWithMetadata
            {
                SongFileName = "album/track2.mp3",
                Price = 3.33m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "album/track2.mp3",
                    ImageBlobPath = "album/track2.jpg",
                    AlbumName = "My Album",
                    TrackNumber = 2,
                    SongPrice = 3.33m
                }
            },
            new CartItemWithMetadata
            {
                SongFileName = "album/track3.mp3",
                Price = 3.33m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "album/track3.mp3",
                    ImageBlobPath = "album/track3.jpg",
                    AlbumName = "My Album",
                    TrackNumber = 3,
                    SongPrice = 3.33m
                }
            }
        };

        // Act
        var result = await _service.SendSongPurchaseConfirmationAsync(
            userEmail,
            userName,
            streamTunesOrderId,
            payPalOrderId,
            purchasedItems,
            totalAmount,
            baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Purchase Confirmation")),
                It.Is<string>(body =>
                    body.Contains("My Album") &&
                    body.Contains("$9.99"))),
            Times.Once);
    }

    [Test]
    public async Task SendSongPurchaseConfirmationAsync_WithMixedItems_IncludesBothSectionsInEmail()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var streamTunesOrderId = "ORD-12345";
        var payPalOrderId = "PP-67890";
        var baseUrl = "https://streamtunes.net";
        var totalAmount = 12.97m;

        var purchasedItems = new List<CartItemWithMetadata>
        {
            // Standalone song
            new CartItemWithMetadata
            {
                SongFileName = "standalone.mp3",
                Price = 0.99m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "songs/standalone.mp3",
                    ImageBlobPath = "songs/standalone.jpg",
                    SongPrice = 0.99m
                }
            },
            // Album tracks
            new CartItemWithMetadata
            {
                SongFileName = "album/track1.mp3",
                Price = 3.99m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "album/track1.mp3",
                    ImageBlobPath = "album/track1.jpg",
                    AlbumName = "Test Album",
                    TrackNumber = 1,
                    SongPrice = 3.99m
                }
            },
            new CartItemWithMetadata
            {
                SongFileName = "album/track2.mp3",
                Price = 3.99m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "album/track2.mp3",
                    ImageBlobPath = "album/track2.jpg",
                    AlbumName = "Test Album",
                    TrackNumber = 2,
                    SongPrice = 3.99m
                }
            },
            new CartItemWithMetadata
            {
                SongFileName = "album/track3.mp3",
                Price = 4.00m,
                SongMetadata = new SongMetadata
                {
                    Mp3BlobPath = "album/track3.mp3",
                    ImageBlobPath = "album/track3.jpg",
                    AlbumName = "Test Album",
                    TrackNumber = 3,
                    SongPrice = 4.00m
                }
            }
        };

        // Act
        var result = await _service.SendSongPurchaseConfirmationAsync(
            userEmail,
            userName,
            streamTunesOrderId,
            payPalOrderId,
            purchasedItems,
            totalAmount,
            baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Purchase Confirmation")),
                It.Is<string>(body =>
                    body.Contains("Individual Songs") &&
                    body.Contains("Test Album") &&
                    body.Contains("$12.97"))),
            Times.Once);
    }

    [Test]
    public async Task SendSubscriptionConfirmationAsync_SendsEmailWithCorrectDetails()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var payPalSubscriptionId = "I-ABCD1234";
        var baseUrl = "https://streamtunes.net";

        var subscription = new Subscription
        {
            Id = 1,
            UserId = 1,
            PayPalSubscriptionId = payPalSubscriptionId,
            Status = "ACTIVE",
            StartDate = DateTime.UtcNow,
            MonthlyPrice = 3.99m,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            EndDate = DateTime.UtcNow.AddMonths(1)
        };

        // Act
        var result = await _service.SendSubscriptionConfirmationAsync(
            userEmail,
            userName,
            subscription,
            payPalSubscriptionId,
            baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Subscription Confirmation")),
                It.Is<string>(body =>
                    body.Contains(payPalSubscriptionId) &&
                    body.Contains("$3.99") &&
                    body.Contains("Monthly Streaming Subscription") &&
                    body.Contains("right to cancel at any time"))),
            Times.Once);
    }

    [Test]
    public async Task SendSongPurchaseConfirmationAsync_WithNullMetadata_HandlesFallback()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var streamTunesOrderId = "ORD-12345";
        var payPalOrderId = "PP-67890";
        var baseUrl = "https://streamtunes.net";
        var totalAmount = 0.99m;

        var purchasedItems = new List<CartItemWithMetadata>
        {
            new CartItemWithMetadata
            {
                SongFileName = "songs/mysong.mp3",
                Price = 0.99m,
                SongMetadata = null // No metadata available
            }
        };

        // Act
        var result = await _service.SendSongPurchaseConfirmationAsync(
            userEmail,
            userName,
            streamTunesOrderId,
            payPalOrderId,
            purchasedItems,
            totalAmount,
            baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Purchase Confirmation")),
                It.Is<string>(body => body.Contains("mysong"))), // Falls back to filename
            Times.Once);
    }

    [Test]
    public async Task SendSongPurchaseConfirmationAsync_WhenEmailServiceFails_ReturnsFalse()
    {
        // Arrange
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var userEmail = "test@example.com";
        var userName = "Test User";
        var streamTunesOrderId = "ORD-12345";
        var payPalOrderId = "PP-67890";
        var baseUrl = "https://streamtunes.net";
        var totalAmount = 0.99m;

        var purchasedItems = new List<CartItemWithMetadata>
        {
            new CartItemWithMetadata
            {
                SongFileName = "song.mp3",
                Price = 0.99m,
                SongMetadata = null
            }
        };

        // Act
        var result = await _service.SendSongPurchaseConfirmationAsync(
            userEmail,
            userName,
            streamTunesOrderId,
            payPalOrderId,
            purchasedItems,
            totalAmount,
            baseUrl);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task SendSubscriptionConfirmationAsync_WhenEmailServiceFails_ReturnsFalse()
    {
        // Arrange
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var userEmail = "test@example.com";
        var userName = "Test User";
        var payPalSubscriptionId = "I-ABCD1234";
        var baseUrl = "https://streamtunes.net";

        var subscription = new Subscription
        {
            Id = 1,
            UserId = 1,
            PayPalSubscriptionId = payPalSubscriptionId,
            Status = "ACTIVE",
            StartDate = DateTime.UtcNow,
            MonthlyPrice = 3.99m,
            NextBillingDate = DateTime.UtcNow.AddMonths(1)
        };

        // Act
        var result = await _service.SendSubscriptionConfirmationAsync(
            userEmail,
            userName,
            subscription,
            payPalSubscriptionId,
            baseUrl);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task SendSongPurchaseConfirmationAsync_IncludesLogoUrl()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var streamTunesOrderId = "ORD-12345";
        var payPalOrderId = "PP-67890";
        var baseUrl = "https://streamtunes.net";
        var totalAmount = 0.99m;

        var purchasedItems = new List<CartItemWithMetadata>
        {
            new CartItemWithMetadata
            {
                SongFileName = "song.mp3",
                Price = 0.99m,
                SongMetadata = null
            }
        };

        // Act
        await _service.SendSongPurchaseConfirmationAsync(
            userEmail,
            userName,
            streamTunesOrderId,
            payPalOrderId,
            purchasedItems,
            totalAmount,
            baseUrl);

        // Assert
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("images/logo-light-small.png"))),
            Times.Once);
    }

    [Test]
    public async Task SendSubscriptionConfirmationAsync_IncludesCancellationTerms()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var payPalSubscriptionId = "I-ABCD1234";
        var baseUrl = "https://streamtunes.net";

        var endDate = DateTime.UtcNow.AddMonths(1);
        var subscription = new Subscription
        {
            Id = 1,
            UserId = 1,
            PayPalSubscriptionId = payPalSubscriptionId,
            Status = "ACTIVE",
            StartDate = DateTime.UtcNow,
            MonthlyPrice = 3.99m,
            NextBillingDate = endDate,
            EndDate = endDate
        };

        // Act
        await _service.SendSubscriptionConfirmationAsync(
            userEmail,
            userName,
            subscription,
            payPalSubscriptionId,
            baseUrl);

        // Assert
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<string>(body =>
                    body.Contains("right to cancel at any time") &&
                    body.Contains("subscription will remain active until your subscription end date"))),
            Times.Once);
    }
}
