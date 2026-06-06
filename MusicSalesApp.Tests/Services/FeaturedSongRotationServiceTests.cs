using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class FeaturedSongRotationServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ILogger<FeaturedSongRotationService>> _mockLogger;
    private FeaturedSongRotationService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"FeaturedSongRotationServiceTests_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);
        _mockLogger = new Mock<ILogger<FeaturedSongRotationService>>();
        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory
            .Setup(factory => factory.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _service = new FeaturedSongRotationService(_mockContextFactory.Object, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task RotateFeaturedSongsAsync_WithFourOrMoreEligibleSongs_SelectsExactlyFour()
    {
        // Arrange
        _context.SongMetadata.AddRange(
            CreateSong(1, "eligible/song-1.mp3"),
            CreateSong(2, "eligible/song-2.mp3"),
            CreateSong(3, "eligible/song-3.mp3"),
            CreateSong(4, "eligible/song-4.mp3"),
            CreateSong(5, "eligible/song-5.mp3"),
            CreateSong(6, "eligible/song-6.mp3"));
        await _context.SaveChangesAsync();

        // Act
        await _service.RotateFeaturedSongsAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata.OrderBy(song => song.Id).ToListAsync();
        var featuredSongs = songs.Where(song => song.DisplayOnHomePage).ToList();

        Assert.That(featuredSongs, Has.Count.EqualTo(4));
        Assert.That(featuredSongs.All(IsEligibleFeaturedSong), Is.True);
    }

    [Test]
    public async Task RotateFeaturedSongsAsync_WithFewerThanFourEligibleSongs_SelectsAllEligibleSongs()
    {
        // Arrange
        _context.SongMetadata.AddRange(
            CreateSong(1, "eligible/song-1.mp3"),
            CreateSong(2, "eligible/song-2.mp3"),
            CreateSong(3, "disabled/song.mp3", isEnabled: false));
        await _context.SaveChangesAsync();

        // Act
        await _service.RotateFeaturedSongsAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var featuredSongIds = await verifyContext.SongMetadata
            .Where(song => song.DisplayOnHomePage)
            .OrderBy(song => song.Id)
            .Select(song => song.Id)
            .ToListAsync();

        Assert.That(featuredSongIds, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task RotateFeaturedSongsAsync_ClearsStaleFeaturedFlagsIncludingIneligibleSongs()
    {
        // Arrange
        _context.SongMetadata.AddRange(
            CreateSong(1, "eligible/song-1.mp3", displayOnHomePage: true),
            CreateSong(2, "eligible/song-2.mp3", displayOnHomePage: true),
            CreateSong(3, "disabled/song.mp3", isEnabled: false, displayOnHomePage: true),
            CreateSong(4, null, imageBlobPath: "covers/song.jpg", displayOnHomePage: true));
        await _context.SaveChangesAsync();

        // Act
        await _service.RotateFeaturedSongsAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata.OrderBy(song => song.Id).ToListAsync();

        Assert.That(songs.Where(song => song.DisplayOnHomePage).Select(song => song.Id), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(songs.Single(song => song.Id == 3).DisplayOnHomePage, Is.False);
        Assert.That(songs.Single(song => song.Id == 4).DisplayOnHomePage, Is.False);
    }

    [Test]
    public async Task RotateFeaturedSongsAsync_DoesNotSelectDisabledInactiveImageOnlyOrInactiveCreatorSongs()
    {
        // Arrange
        var inactiveCreator = await CreateCreatorAsync("inactive-creator@test.com", isActive: false);

        _context.SongMetadata.AddRange(
            CreateSong(1, "eligible/song-1.mp3"),
            CreateSong(2, "eligible/song-2.mp3"),
            CreateSong(3, "disabled/song.mp3", isEnabled: false, displayOnHomePage: true),
            CreateSong(4, "inactive/song.mp3", isActive: false, displayOnHomePage: true),
            CreateSong(5, null, imageBlobPath: "covers/song.jpg", displayOnHomePage: true),
            CreateSong(6, "inactive-creator/song.mp3", creatorId: inactiveCreator.Id, displayOnHomePage: true));
        await _context.SaveChangesAsync();

        // Act
        await _service.RotateFeaturedSongsAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata
            .Include(song => song.Creator)
            .OrderBy(song => song.Id)
            .ToListAsync();

        var featuredSongs = songs.Where(song => song.DisplayOnHomePage).ToList();
        Assert.That(featuredSongs.Select(song => song.Id), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(songs.Where(song => song.Id is 3 or 4 or 5 or 6).All(song => !song.DisplayOnHomePage), Is.True);
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

    private static bool IsEligibleFeaturedSong(SongMetadata song)
    {
        return song.IsActive &&
               song.IsEnabled &&
               !string.IsNullOrEmpty(song.Mp3BlobPath) &&
               (song.CreatorId == null || song.Creator?.IsActive == true);
    }

    private static SongMetadata CreateSong(
        int id,
        string mp3BlobPath,
        string imageBlobPath = null,
        bool isActive = true,
        bool isEnabled = true,
        bool displayOnHomePage = false,
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
            DisplayOnHomePage = displayOnHomePage,
            CreatorId = creatorId
        };
    }
}
