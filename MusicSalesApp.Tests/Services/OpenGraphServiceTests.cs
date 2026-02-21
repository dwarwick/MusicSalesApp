using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class OpenGraphServiceTests
{
    private Mock<ISongMetadataService> _mockSongMetadataService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<IHttpContextAccessor> _mockHttpContextAccessor;
    private Mock<HttpContext> _mockHttpContext;
    private Mock<HttpRequest> _mockHttpRequest;
    private OpenGraphService _service;

    [SetUp]
    public void Setup()
    {
        _mockSongMetadataService = new Mock<ISongMetadataService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        _mockHttpContext = new Mock<HttpContext>();
        _mockHttpRequest = new Mock<HttpRequest>();

        // Setup default configuration
        _mockConfiguration.Setup(c => c["Facebook:AppId"]).Returns("test-app-id");

        // Setup default HTTP context
        _mockHttpRequest.Setup(r => r.Scheme).Returns("https");
        _mockHttpRequest.Setup(r => r.Host).Returns(new HostString("streamtunes.net"));
        _mockHttpRequest.Setup(r => r.PathBase).Returns(new PathString(""));
        _mockHttpRequest.Setup(r => r.Path).Returns(new PathString("/song/test-song"));
        
        _mockHttpContext.Setup(c => c.Request).Returns(_mockHttpRequest.Object);
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns(_mockHttpContext.Object);

        _service = new OpenGraphService(
            _mockSongMetadataService.Object,
            _mockConfiguration.Object,
            _mockHttpContextAccessor.Object
        );
    }

    [Test]
    public async Task GenerateSongMetaTagsAsync_WithValidSong_ReturnsMetaTags()
    {
        // Arrange
        var songTitle = "Test%20Song";
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "Test Song.mp3",
                AlbumName = null, // Standalone song
                Genre = "Rock",
                TrackLength = 245.5
            },
            new SongMetadata
            {
                Id = 2,
                ImageBlobPath = "Test Song.jpg",
                IsAlbumCover = false
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);

        // Act
        var result = await _service.GenerateSongMetaTagsAsync(songTitle);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Contain("fb:app_id"));
        Assert.That(result, Does.Contain("test-app-id"));
        Assert.That(result, Does.Contain("og:type"));
        Assert.That(result, Does.Contain("music.song"));
        Assert.That(result, Does.Contain("og:title"));
        Assert.That(result, Does.Contain("Test Song"));
        Assert.That(result, Does.Contain("og:url"));
        Assert.That(result, Does.Contain("music:genre"));
        Assert.That(result, Does.Contain("Rock"));
        Assert.That(result, Does.Contain("music:duration"));
        Assert.That(result, Does.Contain("245"));
    }

    [Test]
    public async Task GenerateSongMetaTagsAsync_WithNullTitle_ReturnsEmptyString()
    {
        // Act
        var result = await _service.GenerateSongMetaTagsAsync(null);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GenerateSongMetaTagsAsync_WithSongNotFound_ReturnsEmptyString()
    {
        // Arrange
        var songTitle = "NonExistent";
        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(new List<SongMetadata>());

        // Act
        var result = await _service.GenerateSongMetaTagsAsync(songTitle);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GenerateAlbumMetaTagsAsync_WithValidAlbum_ReturnsMetaTags()
    {
        // Arrange
        var albumName = "Test%20Album";
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                ImageBlobPath = "album/Test Album.jpg",
                AlbumName = "Test Album",
                IsAlbumCover = true,
                
            },
            new SongMetadata
            {
                Id = 2,
                Mp3BlobPath = "album/Track 1.mp3",
                AlbumName = "Test Album",
                Genre = "Jazz",
                TrackNumber = 1
            },
            new SongMetadata
            {
                Id = 3,
                Mp3BlobPath = "album/Track 2.mp3",
                AlbumName = "Test Album",
                Genre = "Jazz",
                TrackNumber = 2
            }
        };

        _mockSongMetadataService.Setup(s => s.GetByAlbumNameAsync("Test Album")).ReturnsAsync(metadata);

        // Act
        var result = await _service.GenerateAlbumMetaTagsAsync(albumName);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Contain("fb:app_id"));
        Assert.That(result, Does.Contain("test-app-id"));
        Assert.That(result, Does.Contain("og:type"));
        Assert.That(result, Does.Contain("music.album"));
        Assert.That(result, Does.Contain("og:title"));
        Assert.That(result, Does.Contain("Test Album"));
        Assert.That(result, Does.Contain("og:description"));
        Assert.That(result, Does.Contain("2 tracks"));
        Assert.That(result, Does.Contain("music:genre"));
        Assert.That(result, Does.Contain("Jazz"));
    }

    [Test]
    public async Task GenerateAlbumMetaTagsAsync_WithNullAlbumName_ReturnsEmptyString()
    {
        // Act
        var result = await _service.GenerateAlbumMetaTagsAsync(null);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GenerateAlbumMetaTagsAsync_WithAlbumNotFound_ReturnsEmptyString()
    {
        // Arrange
        var albumName = "NonExistent";
        _mockSongMetadataService.Setup(s => s.GetByAlbumNameAsync(It.IsAny<string>())).ReturnsAsync(new List<SongMetadata>());

        // Act
        var result = await _service.GenerateAlbumMetaTagsAsync(albumName);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GenerateSongMetaTagsAsync_WithSpecialCharacters_EscapesCorrectly()
    {
        // Arrange
        var songTitle = "Song%20with%20%26%20Special%20%3CChars%3E";
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "Song with & Special <Chars>.mp3",
                AlbumName = null,
                Genre = "Rock"
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);

        // Act
        var result = await _service.GenerateSongMetaTagsAsync(songTitle);

        // Assert
        Assert.That(result, Is.Not.Empty);
        // Check that HTML special characters are escaped
        Assert.That(result, Does.Contain("&amp;"));
        Assert.That(result, Does.Contain("&lt;"));
        Assert.That(result, Does.Contain("&gt;"));
    }

    [Test]
    public async Task GenerateSongMetaTagsAsync_WithNoHttpContext_UsesFallbackUrl()
    {
        // Arrange
        var songTitle = "Test%20Song";
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "Test Song.mp3",
                AlbumName = null
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns((HttpContext)null);

        // Act
        var result = await _service.GenerateSongMetaTagsAsync(songTitle);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Contain("https://streamtunes.net"));
    }

    [Test]
    public async Task GenerateSongMetaTagsAsync_WithStoredSongTitle_UsesStoredTitleForOgTitle()
    {
        // Arrange - blob filename differs from the stored SongTitle
        var songTitle = "My%20Great%20Song"; // URL uses stored title
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "my_great_song/my_great_song.mp3",
                SongTitle = "My Great Song", // Human-readable stored title
                AlbumName = null,
                Genre = "Pop",
                TrackLength = 180.0,
                ImageBlobPath = "my_great_song/my_great_song.jpg"
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);

        // Act
        var result = await _service.GenerateSongMetaTagsAsync(songTitle);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Contain("og:title"));
        Assert.That(result, Does.Contain("My Great Song")); // Should use stored SongTitle
        Assert.That(result, Does.Contain("og:image"));
        Assert.That(result, Does.Contain("my_great_song.jpg")); // Should use ImageBlobPath from song record
    }

    [Test]
    public async Task GenerateSongMetaTagsAsync_WithStoredSongTitle_FindsSongBySongTitle()
    {
        // Arrange - song is searched by stored SongTitle, not blob filename
        var songTitle = "Awesome%20Track"; // URL title matches stored SongTitle
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "track001/track001.mp3", // Blob filename does not match URL title
                SongTitle = "Awesome Track",           // Stored title matches URL title
                AlbumName = null,
                Genre = "Jazz"
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);

        // Act
        var result = await _service.GenerateSongMetaTagsAsync(songTitle);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Contain("Awesome Track"));
    }

    [Test]
    public async Task GenerateSongMetaTagsAsync_WithImageOnSongRecord_UsesImageBlobPath()
    {
        // Arrange - image is on the same metadata record as the MP3
        var songTitle = "Test%20Song";
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "Test Song.mp3",
                ImageBlobPath = "Test Song/cover.jpg", // Image on same record
                AlbumName = null,
                Genre = "Rock"
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);

        // Act
        var result = await _service.GenerateSongMetaTagsAsync(songTitle);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Contain("og:image"));
        Assert.That(result, Does.Contain("cover.jpg")); // Should use ImageBlobPath from song record
    }
}
