using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Xml.Linq;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class SitemapServiceTests
{
    private Mock<ISongMetadataService> _mockSongMetadataService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<IWebHostEnvironment> _mockEnvironment;
    private Mock<ILogger<SitemapService>> _mockLogger;
    private SitemapService _service;
    private string _tempDirectory;

    [SetUp]
    public void SetUp()
    {
        _mockSongMetadataService = new Mock<ISongMetadataService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockEnvironment = new Mock<IWebHostEnvironment>();
        _mockLogger = new Mock<ILogger<SitemapService>>();

        // Create a temporary directory for wwwroot
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"SitemapTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);

        // Setup mocks
        _mockConfiguration.Setup(x => x["App:BaseUrl"]).Returns("https://streamtunes.net");
        _mockEnvironment.Setup(x => x.WebRootPath).Returns(_tempDirectory);

        _service = new SitemapService(
            _mockSongMetadataService.Object,
            _mockConfiguration.Object,
            _mockEnvironment.Object,
            _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up temporary directory
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Test]
    public async Task GenerateSitemapAsync_WithNoSongsOrAlbums_CreatesBasicSitemap()
    {
        // Arrange
        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<SongMetadata>());

        // Act
        await _service.GenerateSitemapAsync();

        // Assert
        var sitemapPath = Path.Combine(_tempDirectory, "sitemap.xml");
        Assert.That(File.Exists(sitemapPath), Is.True, "Sitemap file should be created");

        var content = await File.ReadAllTextAsync(sitemapPath);
        Assert.That(content, Does.Contain("<urlset"), "Should contain urlset element");
        Assert.That(content, Does.Contain("https://streamtunes.net"), "Should contain home page URL");
    }

    [Test]
    public async Task GenerateSitemapAsync_WithStandaloneSongs_IncludesSongUrls()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Echoes in the Haze.mp3",
                AlbumName = null, // Standalone song
                UpdatedAt = new DateTime(2024, 1, 1)
            },
            new SongMetadata
            {
                Id = 2,
                Mp3BlobPath = "songs/Summer Vibes.mp3",
                AlbumName = null, // Standalone song
                UpdatedAt = new DateTime(2024, 1, 2)
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(songs);

        // Act
        await _service.GenerateSitemapAsync();

        // Assert
        var sitemapPath = Path.Combine(_tempDirectory, "sitemap.xml");
        var content = await File.ReadAllTextAsync(sitemapPath);

        Assert.That(content, Does.Contain("/song/Echoes%20in%20the%20Haze"), "Should contain URL-encoded song title");
        Assert.That(content, Does.Contain("/song/Summer%20Vibes"), "Should contain second song");
        Assert.That(content, Does.Contain("2024-01-01"), "Should contain lastmod date");
    }

    [Test]
    public async Task GenerateSitemapAsync_WithAlbums_IncludesAlbumUrls()
    {
        // Arrange
        var albums = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                ImageBlobPath = "albums/Best Of 2024/cover.jpg",
                AlbumName = "Best Of 2024",
                IsAlbumCover = true,
                AlbumPrice = 9.99m,
                UpdatedAt = new DateTime(2024, 2, 1)
            },
            new SongMetadata
            {
                Id = 2,
                ImageBlobPath = "albums/Rock Classics/cover.jpg",
                AlbumName = "Rock Classics",
                IsAlbumCover = true,
                AlbumPrice = 12.99m,
                UpdatedAt = new DateTime(2024, 2, 2)
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(albums);

        // Act
        await _service.GenerateSitemapAsync();

        // Assert
        var sitemapPath = Path.Combine(_tempDirectory, "sitemap.xml");
        var content = await File.ReadAllTextAsync(sitemapPath);

        Assert.That(content, Does.Contain("/album/Best%20Of%202024"), "Should contain first album");
        Assert.That(content, Does.Contain("/album/Rock%20Classics"), "Should contain second album");
        Assert.That(content, Does.Contain("2024-02-01"), "Should contain lastmod date");
    }

    [Test]
    public async Task GenerateSitemapAsync_WithMixedContent_IncludesBothSongsAndAlbums()
    {
        // Arrange
        var metadata = new List<SongMetadata>
        {
            // Standalone song
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Solo Track.mp3",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            },
            // Album cover
            new SongMetadata
            {
                Id = 2,
                ImageBlobPath = "albums/My Album/cover.jpg",
                AlbumName = "My Album",
                IsAlbumCover = true,
                AlbumPrice = 9.99m,
                UpdatedAt = DateTime.UtcNow
            },
            // Album track (should not be included as standalone)
            new SongMetadata
            {
                Id = 3,
                Mp3BlobPath = "albums/My Album/Track 1.mp3",
                AlbumName = "My Album",
                TrackNumber = 1,
                UpdatedAt = DateTime.UtcNow
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        await _service.GenerateSitemapAsync();

        // Assert
        var sitemapPath = Path.Combine(_tempDirectory, "sitemap.xml");
        var content = await File.ReadAllTextAsync(sitemapPath);

        // Should include standalone song
        Assert.That(content, Does.Contain("/song/Solo%20Track"), "Should include standalone song");

        // Should include album
        Assert.That(content, Does.Contain("/album/My%20Album"), "Should include album");

        // Should NOT include album tracks as standalone songs
        var songUrlCount = content.Split("/song/").Length - 1;
        Assert.That(songUrlCount, Is.EqualTo(1), "Should only have 1 standalone song URL (not album tracks)");
    }

    [Test]
    public async Task GenerateSitemapAsync_WithSpecialCharacters_EscapesUrlsProperly()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Song & Title.mp3",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(songs);

        // Act
        await _service.GenerateSitemapAsync();

        // Assert
        var sitemapPath = Path.Combine(_tempDirectory, "sitemap.xml");
        var content = await File.ReadAllTextAsync(sitemapPath);

        Assert.That(content, Does.Contain("/song/Song%20%26%20Title"), "Should URL-encode special characters");
    }

    [Test]
    public async Task GenerateSitemapAsync_GeneratesValidXml()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Test.mp3",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(songs);

        // Act
        await _service.GenerateSitemapAsync();

        // Assert
        var sitemapPath = Path.Combine(_tempDirectory, "sitemap.xml");
        var content = await File.ReadAllTextAsync(sitemapPath);

        // Should be valid XML
        Assert.DoesNotThrow(() => XDocument.Parse(content), "Should generate valid XML");

        // Parse and verify structure
        var doc = XDocument.Parse(content);
        var ns = XNamespace.Get("http://www.sitemaps.org/schemas/sitemap/0.9");

        Assert.That(doc.Root?.Name.LocalName, Is.EqualTo("urlset"), "Root should be urlset");
        Assert.That(doc.Root?.Name.Namespace, Is.EqualTo(ns), "Should use correct namespace");

        var urlElements = doc.Descendants(ns + "url").ToList();
        Assert.That(urlElements.Count, Is.GreaterThan(0), "Should have at least one URL");

        // Verify required elements exist
        foreach (var url in urlElements)
        {
            Assert.That(url.Element(ns + "loc"), Is.Not.Null, "Each URL should have a loc element");
            Assert.That(url.Element(ns + "lastmod"), Is.Not.Null, "Each URL should have a lastmod element");
            Assert.That(url.Element(ns + "changefreq"), Is.Not.Null, "Each URL should have a changefreq element");
            Assert.That(url.Element(ns + "priority"), Is.Not.Null, "Each URL should have a priority element");
        }
    }

    [Test]
    public async Task GenerateSitemapAsync_WhenServiceThrows_LogsErrorAndRethrows()
    {
        // Arrange
        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        Assert.ThrowsAsync<Exception>(async () => await _service.GenerateSitemapAsync());
    }

    [Test]
    public async Task GenerateSitemapAsync_UsesConfiguredBaseUrl()
    {
        // Arrange
        _mockConfiguration.Setup(x => x["App:BaseUrl"]).Returns("https://custom-domain.com");

        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Test.mp3",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            }
        };

        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(songs);

        // Act
        await _service.GenerateSitemapAsync();

        // Assert
        var sitemapPath = Path.Combine(_tempDirectory, "sitemap.xml");
        var content = await File.ReadAllTextAsync(sitemapPath);

        Assert.That(content, Does.Contain("https://custom-domain.com"), "Should use configured base URL");
        Assert.That(content, Does.Not.Contain("https://streamtunes.net"), "Should not use default URL");
    }

    [Test]
    public async Task GenerateSitemapAsync_IncludesXmlDeclarationWithUtf8Encoding()
    {
        // Arrange
        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<SongMetadata>());

        // Act
        await _service.GenerateSitemapAsync();

        // Assert
        var sitemapPath = Path.Combine(_tempDirectory, "sitemap.xml");
        var content = await File.ReadAllTextAsync(sitemapPath);

        // Verify the XML declaration is present with UTF-8 encoding
        Assert.That(content, Does.StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>"),
            "Sitemap should include XML declaration with UTF-8 encoding");
    }
}
