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
        int? creatorId = null,
        string genre = null,
        int? personaId = null)
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
            CreatorId = creatorId,
            Genre = genre,
            PersonaId = personaId
        };
    }

    private async Task<CreatorPersona> CreatePersonaAsync(
        int creatorId, bool isEnabled = true, string imageBlobPath = "personas/test.jpg", string name = "Test Persona")
    {
        var persona = new CreatorPersona
        {
            CreatorId = creatorId,
            Name = name,
            ImageBlobPath = imageBlobPath,
            IsEnabled = isEnabled
        };
        _context.CreatorPersonas.Add(persona);
        await _context.SaveChangesAsync();
        return persona;
    }

    [Test]
    public async Task RandomizeDisplayOrderAsync_RanksCompletenessTiersFromMostToLeastComplete()
    {
        // Arrange: one song per tier, so exact ordering is deterministic
        var creator = await CreateCreatorAsync("test@example.com", isActive: true);
        var completePersona = await CreatePersonaAsync(creator.Id, isEnabled: true, name: "Complete", imageBlobPath: "personas/complete.jpg");

        _context.SongMetadata.AddRange(
            // 100% complete
            CreateSong(1, "song-1.mp3", imageBlobPath: "covers/song-1.jpg", genre: "Rock", personaId: completePersona.Id),
            // 50% complete (cover + genre only)
            CreateSong(2, "song-2.mp3", imageBlobPath: "covers/song-2.jpg", genre: "Rock"),
            // 25% complete (cover art only)
            CreateSong(3, "song-3.mp3", imageBlobPath: "covers/song-3.jpg"),
            // 0% complete (no cover art, even though genre and persona are set)
            CreateSong(4, "song-4.mp3", genre: "Rock", personaId: completePersona.Id));
        await _context.SaveChangesAsync();

        // Act
        await _service.RandomizeDisplayOrderAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata.OrderBy(song => song.Id).ToListAsync();

        var song1 = songs.Single(s => s.Id == 1);
        var song2 = songs.Single(s => s.Id == 2);
        var song3 = songs.Single(s => s.Id == 3);
        var song4 = songs.Single(s => s.Id == 4);

        Assert.That(song1.DisplayOrder, Is.LessThan(song2.DisplayOrder));
        Assert.That(song2.DisplayOrder, Is.LessThan(song3.DisplayOrder));
        Assert.That(song3.DisplayOrder, Is.LessThan(song4.DisplayOrder));
    }

    [Test]
    public async Task RandomizeDisplayOrderAsync_MissingCoverArtAlwaysRanksBelowSongsThatHaveCoverArt()
    {
        // Arrange: prove the hard override
        // Song A: just cover art (25%)
        // Song B: no cover art, but genre + complete enabled persona (would be 75% without override, forced to 0%)
        var creator = await CreateCreatorAsync("test@example.com", isActive: true);
        var completePersona = await CreatePersonaAsync(creator.Id, isEnabled: true, name: "Test", imageBlobPath: "personas/test.jpg");

        _context.SongMetadata.AddRange(
            CreateSong(1, "song-a.mp3", imageBlobPath: "covers/song-a.jpg"),
            CreateSong(2, "song-b.mp3", genre: "Rock", personaId: completePersona.Id));
        await _context.SaveChangesAsync();

        // Act
        await _service.RandomizeDisplayOrderAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata.OrderBy(song => song.Id).ToListAsync();

        var songA = songs.Single(s => s.Id == 1);
        var songB = songs.Single(s => s.Id == 2);

        Assert.That(songA.DisplayOrder, Is.LessThan(songB.DisplayOrder),
            "Song with cover art (25%) must rank above song with no cover art (forced to 0%), regardless of other fields");
    }

    [Test]
    public async Task RandomizeDisplayOrderAsync_WithMultipleSongsPerTier_KeepsTierBoundariesMonotonicWithoutFixingOrderWithinTier()
    {
        // Arrange: 3 songs each in 100% / 50% / 0% tiers (9 songs)
        var creator = await CreateCreatorAsync("test@example.com", isActive: true);
        var persona = await CreatePersonaAsync(creator.Id, isEnabled: true, name: "Test", imageBlobPath: "personas/test.jpg");

        // Tier 100% (ids 1-3)
        _context.SongMetadata.AddRange(
            CreateSong(1, "song-1.mp3", imageBlobPath: "covers/1.jpg", genre: "Rock", personaId: persona.Id),
            CreateSong(2, "song-2.mp3", imageBlobPath: "covers/2.jpg", genre: "Rock", personaId: persona.Id),
            CreateSong(3, "song-3.mp3", imageBlobPath: "covers/3.jpg", genre: "Rock", personaId: persona.Id));

        // Tier 50% (ids 4-6)
        _context.SongMetadata.AddRange(
            CreateSong(4, "song-4.mp3", imageBlobPath: "covers/4.jpg", genre: "Rock"),
            CreateSong(5, "song-5.mp3", imageBlobPath: "covers/5.jpg", genre: "Rock"),
            CreateSong(6, "song-6.mp3", imageBlobPath: "covers/6.jpg", genre: "Rock"));

        // Tier 0% (ids 7-9)
        _context.SongMetadata.AddRange(
            CreateSong(7, "song-7.mp3", genre: "Rock", personaId: persona.Id),
            CreateSong(8, "song-8.mp3", genre: "Rock", personaId: persona.Id),
            CreateSong(9, "song-9.mp3", genre: "Rock", personaId: persona.Id));

        await _context.SaveChangesAsync();

        // Act
        await _service.RandomizeDisplayOrderAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata.OrderBy(song => song.Id).ToListAsync();

        var tier100Max = songs.Where(s => s.Id is 1 or 2 or 3).Max(s => s.DisplayOrder);
        var tier50Min = songs.Where(s => s.Id is 4 or 5 or 6).Min(s => s.DisplayOrder);
        var tier50Max = songs.Where(s => s.Id is 4 or 5 or 6).Max(s => s.DisplayOrder);
        var tier0Min = songs.Where(s => s.Id is 7 or 8 or 9).Min(s => s.DisplayOrder);

        Assert.That(tier100Max, Is.LessThan(tier50Min), "100% tier must rank above 50% tier");
        Assert.That(tier50Max, Is.LessThan(tier0Min), "50% tier must rank above 0% tier");
        Assert.That(songs.Select(s => s.DisplayOrder).OrderBy(x => x), Is.EqualTo(Enumerable.Range(1, 9)));
    }

    [Test]
    public async Task RandomizeDisplayOrderAsync_DisabledPersonaIsTreatedAsIncompleteForTierPlacement()
    {
        // Arrange
        var creator = await CreateCreatorAsync("test@example.com", isActive: true);
        var enabledPersona = await CreatePersonaAsync(creator.Id, isEnabled: true, name: "Enabled", imageBlobPath: "personas/enabled.jpg");
        var disabledPersona = await CreatePersonaAsync(creator.Id, isEnabled: false, name: "Disabled", imageBlobPath: "personas/disabled.jpg");

        _context.SongMetadata.AddRange(
            // 100% complete: all 4 fields with enabled persona
            CreateSong(1, "song-1.mp3", imageBlobPath: "covers/song-1.jpg", genre: "Rock", personaId: enabledPersona.Id),
            // 50% complete: cover + genre, but disabled persona means persona fields don't count
            CreateSong(2, "song-2.mp3", imageBlobPath: "covers/song-2.jpg", genre: "Rock", personaId: disabledPersona.Id));
        await _context.SaveChangesAsync();

        // Act
        await _service.RandomizeDisplayOrderAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata
            .Include(song => song.Persona)
            .OrderBy(song => song.Id)
            .ToListAsync();

        var song1 = songs.Single(s => s.Id == 1);
        var song2 = songs.Single(s => s.Id == 2);

        Assert.That(song1.DisplayOrder, Is.LessThan(song2.DisplayOrder),
            "Enabled persona should allow 100% score; disabled persona should cap at 50%");
    }
}