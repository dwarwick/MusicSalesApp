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
                
                UpdatedAt = new DateTime(2024, 2, 1)
            },
            new SongMetadata
            {
                Id = 2,
                ImageBlobPath = "albums/Rock Classics/cover.jpg",
                AlbumName = "Rock Classics",
                IsAlbumCover = true,
                
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

    [Test]
    public async Task GenerateSitemapAsync_IncludesStaticPolicyPages()
    {
        // Arrange
        _mockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<SongMetadata>());

        // Act
        await _service.GenerateSitemapAsync();

        // Assert
        var sitemapPath = Path.Combine(_tempDirectory, "sitemap.xml");
        var content = await File.ReadAllTextAsync(sitemapPath);

        // Verify all static policy pages are included
        Assert.That(content, Does.Contain("/privacy-policy"), "Should include Privacy Policy page");
        Assert.That(content, Does.Contain("/terms-of-use"), "Should include Terms of Use page");
        Assert.That(content, Does.Contain("/creator-agreement"), "Should include Creator Agreement page");
        Assert.That(content, Does.Contain("/user-refund-policy"), "Should include User Refund Policy page");
    }

    [Test]
    public async Task GenerateSitemapAsync_IncludesArtistUrls()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Test Song.mp3",
                ArtistName = "Garth Brooks",
                Genre = "Country",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            },
            new SongMetadata
            {
                Id = 2,
                Mp3BlobPath = "songs/Another Song.mp3",
                ArtistName = "Chris Warwick",
                Genre = "Rock",
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

        Assert.That(content, Does.Contain("/artist/Garth%20Brooks"), "Should include URL-encoded artist Garth Brooks");
        Assert.That(content, Does.Contain("/artist/Chris%20Warwick"), "Should include URL-encoded artist Chris Warwick");
    }

    [Test]
    public async Task GenerateSitemapAsync_IncludesGenreUrls()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Country Song.mp3",
                ArtistName = "Artist 1",
                Genre = "Country",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            },
            new SongMetadata
            {
                Id = 2,
                Mp3BlobPath = "songs/Rock Song.mp3",
                ArtistName = "Artist 2",
                Genre = "Rock",
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

        Assert.That(content, Does.Contain("/genre/Country"), "Should include genre Country");
        Assert.That(content, Does.Contain("/genre/Rock"), "Should include genre Rock");
    }

    [Test]
    public async Task GenerateSitemapAsync_DeduplicatesArtistUrls()
    {
        // Arrange - Two songs by the same artist
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Song 1.mp3",
                ArtistName = "Garth Brooks",
                Genre = "Country",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            },
            new SongMetadata
            {
                Id = 2,
                Mp3BlobPath = "songs/Song 2.mp3",
                ArtistName = "Garth Brooks",
                Genre = "Country",
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

        // Should contain only one artist URL (deduplicated)
        var artistUrlCount = content.Split("/artist/").Length - 1;
        Assert.That(artistUrlCount, Is.EqualTo(1), "Should have only 1 artist URL (deduplicated)");
    }

    [Test]
    public async Task GenerateSitemapAsync_DeduplicatesGenreUrls()
    {
        // Arrange - Two songs in the same genre
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Song 1.mp3",
                ArtistName = "Artist 1",
                Genre = "Country",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            },
            new SongMetadata
            {
                Id = 2,
                Mp3BlobPath = "songs/Song 2.mp3",
                ArtistName = "Artist 2",
                Genre = "Country",
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

        // Should contain only one genre URL (deduplicated)
        var genreUrlCount = content.Split("/genre/").Length - 1;
        Assert.That(genreUrlCount, Is.EqualTo(1), "Should have only 1 genre URL (deduplicated)");
    }

    [Test]
    public async Task GenerateSitemapAsync_ExcludesEmptyArtistNames()
    {
        // Arrange - Song without artist name
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Test.mp3",
                ArtistName = null, // No artist name
                Genre = "Country",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            },
            new SongMetadata
            {
                Id = 2,
                Mp3BlobPath = "songs/Test2.mp3",
                ArtistName = "", // Empty artist name
                Genre = "Rock",
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

        // Should not contain any artist URL
        var artistUrlCount = content.Split("/artist/").Length - 1;
        Assert.That(artistUrlCount, Is.EqualTo(0), "Should have no artist URLs when all artist names are empty");
    }

    [Test]
    public async Task GenerateSitemapAsync_ExcludesEmptyGenres()
    {
        // Arrange - Songs without genres
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Test.mp3",
                ArtistName = "Artist",
                Genre = null, // No genre
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            },
            new SongMetadata
            {
                Id = 2,
                Mp3BlobPath = "songs/Test2.mp3",
                ArtistName = "Artist 2",
                Genre = "", // Empty genre
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

        // Should not contain any genre URL
        var genreUrlCount = content.Split("/genre/").Length - 1;
        Assert.That(genreUrlCount, Is.EqualTo(0), "Should have no genre URLs when all genres are empty");
    }

    [Test]
    public async Task GenerateSitemapAsync_OnlyIncludesArtistsFromSongsWithMp3Files()
    {
        // Arrange - Mix of songs and album covers
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Test.mp3",
                ArtistName = "MP3 Artist",
                Genre = "Country",
                AlbumName = null,
                UpdatedAt = DateTime.UtcNow
            },
            new SongMetadata
            {
                Id = 2,
                ImageBlobPath = "albums/Cover/cover.jpg", // Album cover (no MP3)
                AlbumName = "My Album",
                IsAlbumCover = true,
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

        Assert.That(content, Does.Contain("/artist/MP3%20Artist"), "Should include artist from MP3 file");
        var artistUrlCount = content.Split("/artist/").Length - 1;
        Assert.That(artistUrlCount, Is.EqualTo(1), "Should have only 1 artist URL from MP3 file");
    }

    [Test]
    public async Task GenerateSitemapAsync_UrlEncodesSpecialCharactersInArtistAndGenre()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "songs/Test.mp3",
                ArtistName = "Artist & Friends",
                Genre = "Rock & Roll",
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

        Assert.That(content, Does.Contain("/artist/Artist%20%26%20Friends"), "Should URL-encode special characters in artist name");
        Assert.That(content, Does.Contain("/genre/Rock%20%26%20Roll"), "Should URL-encode special characters in genre");
    }
}
