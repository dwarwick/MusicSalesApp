using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class SongDisplayOrderServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ILogger<SongDisplayOrderService>> _mockLogger;
    private SongDisplayOrderService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"SongDisplayOrderServiceTests_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);
        _mockLogger = new Mock<ILogger<SongDisplayOrderService>>();
        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory
            .Setup(factory => factory.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _service = new SongDisplayOrderService(_mockContextFactory.Object, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task RandomizeDisplayOrderAsync_AssignsSequentialOrderToEligibleSongsOnly()
    {
        // Arrange
        var inactiveCreator = await CreateCreatorAsync("inactive-creator@test.com", isActive: false);

        _context.SongMetadata.AddRange(
            CreateSong(1, "eligible/song-1.mp3", displayOrder: 50),
            CreateSong(2, "eligible/song-2.mp3"),
            CreateSong(3, "eligible/song-3.mp3", displayOrder: 7),
            CreateSong(4, "disabled/song.mp3", isEnabled: false, displayOrder: 90),
            CreateSong(5, "inactive/song.mp3", isActive: false, displayOrder: 91),
            CreateSong(6, null, imageBlobPath: "covers/song.jpg", displayOrder: 92),
            CreateSong(7, "creator-inactive/song.mp3", creatorId: inactiveCreator.Id, displayOrder: 93));
        await _context.SaveChangesAsync();

        // Act
        await _service.RandomizeDisplayOrderAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata.OrderBy(song => song.Id).ToListAsync();
        var eligibleSongs = songs.Where(song => song.Id is 1 or 2 or 3).ToList();
        var eligibleDisplayOrders = eligibleSongs
            .Select(song => song.DisplayOrder)
            .OrderBy(displayOrder => displayOrder)
            .ToList();

        Assert.That(eligibleDisplayOrders, Is.EqualTo(new int?[] { 1, 2, 3 }));
        Assert.That(songs.Single(song => song.Id == 4).DisplayOrder, Is.EqualTo(90));
        Assert.That(songs.Single(song => song.Id == 5).DisplayOrder, Is.EqualTo(91));
        Assert.That(songs.Single(song => song.Id == 6).DisplayOrder, Is.EqualTo(92));
        Assert.That(songs.Single(song => song.Id == 7).DisplayOrder, Is.EqualTo(93));
    }

    [Test]
    public async Task RandomizeDisplayOrderAsync_WithNoEligibleSongs_LeavesRowsUnchanged()
    {
        // Arrange
        _context.SongMetadata.AddRange(
            CreateSong(1, "disabled/song.mp3", isEnabled: false, displayOrder: 10),
            CreateSong(2, null, imageBlobPath: "covers/song.jpg", displayOrder: 20));
        await _context.SaveChangesAsync();

        // Act
        await _service.RandomizeDisplayOrderAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata.OrderBy(song => song.Id).ToListAsync();
        Assert.That(songs.Select(song => song.DisplayOrder), Is.EqualTo(new int?[] { 10, 20 }));
    }

    private async Task<Creator> CreateCreatorAsync(string email, bool isActive)
    {
        var user = new ApplicationUser
        {
            Email = email,
            UserName = email
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            User = user,
            UserId = user.Id,
            IsActive = isActive,
            PayPalEmail = $"paypal-{user.Id}@example.com"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        return creator;
    }

    private static SongMetadata CreateSong(
        int id,
        string mp3BlobPath,
        string imageBlobPath = null,
        bool isActive = true,
        bool isEnabled = true,
        int? displayOrder = null,
        int? creatorId = null)
    {
        return new SongMetadata
        {
            Id = id,
            BlobPath = mp3BlobPath ?? imageBlobPath,
            Mp3BlobPath = mp3BlobPath,
            ImageBlobPath = imageBlobPath,
            IsActive = isActive,
            IsEnabled = isEnabled,
            DisplayOrder = displayOrder,
            CreatorId = creatorId
        };
    }
}