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
        var creator = await CreateCreatorAsync("test@example.com", isActive: true);
        var persona = await CreatePersonaAsync(creator.Id);

        _context.SongMetadata.AddRange(
            CreateSong(1, "eligible/song-1.mp3", imageBlobPath: "covers/song-1.jpg", genre: "Rock", personaId: persona.Id),
            CreateSong(2, "eligible/song-2.mp3", imageBlobPath: "covers/song-2.jpg", genre: "Rock", personaId: persona.Id),
            CreateSong(3, "eligible/song-3.mp3", imageBlobPath: "covers/song-3.jpg", genre: "Rock", personaId: persona.Id),
            CreateSong(4, "eligible/song-4.mp3", imageBlobPath: "covers/song-4.jpg", genre: "Rock", personaId: persona.Id),
            CreateSong(5, "eligible/song-5.mp3", imageBlobPath: "covers/song-5.jpg", genre: "Rock", personaId: persona.Id),
            CreateSong(6, "eligible/song-6.mp3", imageBlobPath: "covers/song-6.jpg", genre: "Rock", personaId: persona.Id));
        await _context.SaveChangesAsync();

        // Act
        await _service.RotateFeaturedSongsAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata
            .Include(song => song.Persona)
            .OrderBy(song => song.Id)
            .ToListAsync();
        var featuredSongs = songs.Where(song => song.DisplayOnHomePage).ToList();

        Assert.That(featuredSongs, Has.Count.EqualTo(4));
        Assert.That(featuredSongs.All(IsEligibleFeaturedSong), Is.True);
    }

    [Test]
    public async Task RotateFeaturedSongsAsync_WithFewerThanFourEligibleSongs_SelectsAllEligibleSongs()
    {
        // Arrange
        var creator = await CreateCreatorAsync("test@example.com", isActive: true);
        var persona = await CreatePersonaAsync(creator.Id);

        _context.SongMetadata.AddRange(
            CreateSong(1, "eligible/song-1.mp3", imageBlobPath: "covers/song-1.jpg", genre: "Rock", personaId: persona.Id),
            CreateSong(2, "eligible/song-2.mp3", imageBlobPath: "covers/song-2.jpg", genre: "Rock", personaId: persona.Id),
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
        var creator = await CreateCreatorAsync("test@example.com", isActive: true);
        var persona = await CreatePersonaAsync(creator.Id);

        _context.SongMetadata.AddRange(
            CreateSong(1, "eligible/song-1.mp3", imageBlobPath: "covers/song-1.jpg", genre: "Rock", displayOnHomePage: true, personaId: persona.Id),
            CreateSong(2, "eligible/song-2.mp3", imageBlobPath: "covers/song-2.jpg", genre: "Rock", displayOnHomePage: true, personaId: persona.Id),
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
        var activeCreator = await CreateCreatorAsync("active-creator@test.com", isActive: true);
        var inactiveCreator = await CreateCreatorAsync("inactive-creator@test.com", isActive: false);
        var activePersona = await CreatePersonaAsync(activeCreator.Id);
        var inactivePersona = await CreatePersonaAsync(inactiveCreator.Id);

        _context.SongMetadata.AddRange(
            CreateSong(1, "eligible/song-1.mp3", imageBlobPath: "covers/song-1.jpg", genre: "Rock", personaId: activePersona.Id),
            CreateSong(2, "eligible/song-2.mp3", imageBlobPath: "covers/song-2.jpg", genre: "Rock", personaId: activePersona.Id),
            CreateSong(3, "disabled/song.mp3", isEnabled: false, displayOnHomePage: true),
            CreateSong(4, "inactive/song.mp3", isActive: false, displayOnHomePage: true),
            CreateSong(5, null, imageBlobPath: "covers/song.jpg", displayOnHomePage: true),
            CreateSong(6, "inactive-creator/song.mp3", creatorId: inactiveCreator.Id, displayOnHomePage: true, imageBlobPath: "covers/song-6.jpg", genre: "Rock", personaId: inactivePersona.Id));
        await _context.SaveChangesAsync();

        // Act
        await _service.RotateFeaturedSongsAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata
            .Include(song => song.Creator)
            .Include(song => song.Persona)
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

    private async Task<CreatorPersona> CreatePersonaAsync(
        int creatorId, bool isEnabled = true, string imageBlobPath = "personas/test.jpg")
    {
        var persona = new CreatorPersona
        {
            CreatorId = creatorId,
            Name = "Test Persona",
            ImageBlobPath = imageBlobPath,
            IsEnabled = isEnabled
        };
        _context.CreatorPersonas.Add(persona);
        await _context.SaveChangesAsync();

        return persona;
    }

    [Test]
    public async Task RotateFeaturedSongsAsync_ExcludesSongsWithIncompleteProfile()
    {
        // Arrange
        var creator = await CreateCreatorAsync("test@example.com", isActive: true);
        var enabledPersona = await CreatePersonaAsync(creator.Id, isEnabled: true);
        var disabledPersona = await CreatePersonaAsync(creator.Id, isEnabled: false, imageBlobPath: "personas/disabled.jpg");

        _context.SongMetadata.AddRange(
            // Fully complete control song - should be featured
            CreateSong(1, "complete/song.mp3", imageBlobPath: "covers/complete.jpg", genre: "Rock", personaId: enabledPersona.Id),
            // Missing cover art
            CreateSong(2, "no-cover/song.mp3", imageBlobPath: null, genre: "Rock", personaId: enabledPersona.Id),
            // Missing genre
            CreateSong(3, "no-genre/song.mp3", imageBlobPath: "covers/song-3.jpg", genre: null, personaId: enabledPersona.Id),
            // No persona linked
            CreateSong(4, "no-persona/song.mp3", imageBlobPath: "covers/song-4.jpg", genre: "Rock", personaId: null),
            // Persona missing image
            CreateSong(5, "persona-no-image/song.mp3", imageBlobPath: "covers/song-5.jpg", genre: "Rock", personaId: enabledPersona.Id),
            // Persona disabled
            CreateSong(6, "disabled-persona/song.mp3", imageBlobPath: "covers/song-6.jpg", genre: "Rock", personaId: disabledPersona.Id));
        await _context.SaveChangesAsync();

        // Manually clear the image from enabledPersona's copy in the context for song 5
        // (This is a data-setup detail: song 5 uses the same persona but we need to test
        // persona-without-image separately, which requires a different persona instance)
        var song5Persona = new CreatorPersona
        {
            CreatorId = creator.Id,
            Name = "No Image Persona",
            ImageBlobPath = null,
            IsEnabled = true
        };
        _context.CreatorPersonas.Add(song5Persona);
        await _context.SaveChangesAsync();
        _context.SongMetadata.Single(s => s.Id == 5).PersonaId = song5Persona.Id;
        await _context.SaveChangesAsync();

        // Act
        await _service.RotateFeaturedSongsAsync();

        // Assert
        await using var verifyContext = new AppDbContext(_contextOptions);
        var songs = await verifyContext.SongMetadata
            .Include(song => song.Persona)
            .OrderBy(song => song.Id)
            .ToListAsync();

        var featuredSongs = songs.Where(song => song.DisplayOnHomePage).ToList();
        Assert.That(featuredSongs.Select(song => song.Id), Is.EqualTo(new[] { 1 }), "Only the complete song should be featured");
        Assert.That(songs.Where(song => song.Id is 2 or 3 or 4 or 5 or 6).All(song => !song.DisplayOnHomePage), Is.True);
    }

    private static bool IsEligibleFeaturedSong(SongMetadata song)
    {
        return song.IsActive &&
               song.IsEnabled &&
               !string.IsNullOrEmpty(song.Mp3BlobPath) &&
               (song.CreatorId == null || song.Creator?.IsActive == true) &&
               !string.IsNullOrEmpty(song.ImageBlobPath) &&
               !string.IsNullOrEmpty(song.Genre) &&
               song.Persona?.IsEnabled == true &&
               !string.IsNullOrEmpty(song.Persona.Name) &&
               !string.IsNullOrEmpty(song.Persona.ImageBlobPath);
    }

    private static SongMetadata CreateSong(
        int id,
        string mp3BlobPath,
        string imageBlobPath = null,
        bool isActive = true,
        bool isEnabled = true,
        bool displayOnHomePage = false,
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
            DisplayOnHomePage = displayOnHomePage,
            CreatorId = creatorId,
            Genre = genre,
            PersonaId = personaId
        };
    }
}
