using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class GenreServiceTests
{
    private Mock<ILogger<GenreService>> _mockLogger;
    private DbContextOptions<AppDbContext> _dbOptions;
    private IDbContextFactory<AppDbContext> _contextFactory;
    private GenreService _service;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<GenreService>>();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        var mockFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_dbOptions));

        _contextFactory = mockFactory.Object;
        _service = new GenreService(_contextFactory, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Database.EnsureDeleted();
    }

    [Test]
    public async Task GetActiveGenresAsync_ReturnsOnlyActiveGenres()
    {
        // Arrange
        using (var context = new AppDbContext(_dbOptions))
        {
            context.Genres.AddRange(
                new Genre { Name = "Rock", IsActive = true },
                new Genre { Name = "Jazz", IsActive = true },
                new Genre { Name = "Disabled", IsActive = false }
            );
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _service.GetActiveGenresAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(g => g.IsActive), Is.True);
    }

    [Test]
    public async Task GetActiveGenresAsync_ReturnsAlphabeticalOrder()
    {
        // Arrange
        using (var context = new AppDbContext(_dbOptions))
        {
            context.Genres.AddRange(
                new Genre { Name = "Rock", IsActive = true },
                new Genre { Name = "Country", IsActive = true },
                new Genre { Name = "Jazz", IsActive = true }
            );
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _service.GetActiveGenresAsync();

        // Assert
        Assert.That(result[0].Name, Is.EqualTo("Country"));
        Assert.That(result[1].Name, Is.EqualTo("Jazz"));
        Assert.That(result[2].Name, Is.EqualTo("Rock"));
    }

    [Test]
    public async Task GetAllGenresAsync_ReturnsAllGenresIncludingInactive()
    {
        // Arrange
        using (var context = new AppDbContext(_dbOptions))
        {
            context.Genres.AddRange(
                new Genre { Name = "Rock", IsActive = true },
                new Genre { Name = "Disabled", IsActive = false }
            );
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _service.GetAllGenresAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task AddGenreAsync_AddsNewGenre()
    {
        // Act
        var result = await _service.AddGenreAsync("Rock", "user@test.com");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("Rock"));
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.CreatedByEmail, Is.EqualTo("user@test.com"));
    }

    [Test]
    public async Task AddGenreAsync_ReturnsNull_WhenDuplicateGenre()
    {
        // Arrange
        using (var context = new AppDbContext(_dbOptions))
        {
            context.Genres.Add(new Genre { Name = "Rock", IsActive = true });
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _service.AddGenreAsync("Rock", "user@test.com");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task AddGenreAsync_ReturnsNull_WhenDuplicateCaseInsensitive()
    {
        // Arrange
        using (var context = new AppDbContext(_dbOptions))
        {
            context.Genres.Add(new Genre { Name = "Rock", IsActive = true });
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _service.AddGenreAsync("rock", "user@test.com");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task AddGenreAsync_TrimsName()
    {
        // Act
        var result = await _service.AddGenreAsync("  Rock  ", "user@test.com");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("Rock"));
    }

    [Test]
    public async Task AddGenreAsync_ReturnsNull_WhenNameIsEmpty()
    {
        // Act
        var result = await _service.AddGenreAsync("", "user@test.com");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task AddGenreAsync_ReturnsNull_WhenNameIsWhitespace()
    {
        // Act
        var result = await _service.AddGenreAsync("   ", "user@test.com");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task DisableGenreAsync_DisablesGenre()
    {
        // Arrange
        int genreId;
        using (var context = new AppDbContext(_dbOptions))
        {
            var genre = new Genre { Name = "Rock", IsActive = true };
            context.Genres.Add(genre);
            await context.SaveChangesAsync();
            genreId = genre.Id;
        }

        // Act
        var result = await _service.DisableGenreAsync(genreId);

        // Assert
        Assert.That(result, Is.True);
        using (var context = new AppDbContext(_dbOptions))
        {
            var genre = await context.Genres.FindAsync(genreId);
            Assert.That(genre.IsActive, Is.False);
        }
    }

    [Test]
    public async Task DisableGenreAsync_ReturnsFalse_WhenGenreNotFound()
    {
        // Act
        var result = await _service.DisableGenreAsync(999);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task GetByIdAsync_ReturnsGenre()
    {
        // Arrange
        int genreId;
        using (var context = new AppDbContext(_dbOptions))
        {
            var genre = new Genre { Name = "Rock", IsActive = true };
            context.Genres.Add(genre);
            await context.SaveChangesAsync();
            genreId = genre.Id;
        }

        // Act
        var result = await _service.GetByIdAsync(genreId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("Rock"));
    }

    [Test]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Act
        var result = await _service.GetByIdAsync(999);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetByNameAsync_ReturnsGenre_CaseInsensitive()
    {
        // Arrange
        using (var context = new AppDbContext(_dbOptions))
        {
            context.Genres.Add(new Genre { Name = "Rock", IsActive = true });
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _service.GetByNameAsync("rock");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("Rock"));
    }

    [Test]
    public async Task GetByNameAsync_ReturnsNull_WhenNotFound()
    {
        // Act
        var result = await _service.GetByNameAsync("NonExistent");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetByNameAsync_ReturnsNull_WhenNameIsEmpty()
    {
        // Act
        var result = await _service.GetByNameAsync("");

        // Assert
        Assert.That(result, Is.Null);
    }
}
