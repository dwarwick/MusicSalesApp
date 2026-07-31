using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private Mock<IAzureStorageService> _mockStorageService;
    private Mock<ILogger<OpenGraphService>> _mockLogger;
    private OpenGraphService _service;

    [SetUp]
    public void Setup()
    {
        _mockSongMetadataService = new Mock<ISongMetadataService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        _mockHttpContext = new Mock<HttpContext>();
        _mockHttpRequest = new Mock<HttpRequest>();
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockLogger = new Mock<ILogger<OpenGraphService>>();

        // Setup default configuration
        _mockConfiguration.Setup(c => c["Facebook:AppId"]).Returns("test-app-id");

        // Setup default HTTP context
        _mockHttpRequest.Setup(r => r.Scheme).Returns("https");
        _mockHttpRequest.Setup(r => r.Host).Returns(new HostString("streamtunes.net"));
        _mockHttpRequest.Setup(r => r.PathBase).Returns(new PathString(""));
        _mockHttpRequest.Setup(r => r.Path).Returns(new PathString("/song/test-song"));
        
        _mockHttpContext.Setup(c => c.Request).Returns(_mockHttpRequest.Object);
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns(_mockHttpContext.Object);

        // Setup storage service to return false for ExistsAsync by default (no FB image exists)
        _mockStorageService.Setup(s => s.ExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
        // Setup storage service to return empty stream for DownloadAsync by default
        _mockStorageService.Setup(s => s.DownloadAsync(It.IsAny<string>())).ReturnsAsync(new MemoryStream());
        // Setup storage service to return a SAS URI containing the blob path
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns((string path, TimeSpan _) => new Uri($"https://blob.azure.test/{path}?sv=2024&sig=test"));

        _service = new OpenGraphService(
            _mockSongMetadataService.Object,
            _mockConfiguration.Object,
            _mockHttpContextAccessor.Object,
            _mockStorageService.Object,
            _mockLogger.Object
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

    [Test]
    public void GetFacebookImagePath_WithFolderPath_ReturnsCorrectPath()
    {
        var result = OpenGraphService.GetFacebookImagePath("folder/image.jpg");
        Assert.That(result, Is.EqualTo("folder/image_fb.jpg"));
    }

    [Test]
    public void GetFacebookImagePath_WithoutFolder_ReturnsCorrectPath()
    {
        var result = OpenGraphService.GetFacebookImagePath("image.jpg");
        Assert.That(result, Is.EqualTo("image_fb.jpg"));
    }

    [Test]
    public void GetFacebookImagePath_WithNestedFolder_ReturnsCorrectPath()
    {
        var result = OpenGraphService.GetFacebookImagePath("artist/album/cover.png");
        Assert.That(result, Is.EqualTo("artist/album/cover_fb.jpg"));
    }

    [Test]
    public void GetFacebookImagePath_GuidSchemeCoverArt_ReturnsTheSongsFixedSharingImage()
    {
        var mediaGuid = Guid.NewGuid();

        var result = OpenGraphService.GetFacebookImagePath(
            Common.Helpers.SongMediaPaths.CoverArt(mediaGuid, ".jpg"));

        Assert.That(result, Is.EqualTo(Common.Helpers.SongMediaPaths.FacebookImage(mediaGuid)));
    }

    [Test]
    public async Task InvalidateFacebookImageAsync_GuidSchemeCoverArt_DeletesTheFixedSharingImage()
    {
        // The sharing image name does not change when the art is replaced, so the stale blob has
        // to be removed or GetOrCreate's "already exists" short-circuit serves it forever.
        var mediaGuid = Guid.NewGuid();

        await _service.InvalidateFacebookImageAsync(Common.Helpers.SongMediaPaths.CoverArt(mediaGuid, ".png"));

        _mockStorageService.Verify(
            service => service.DeleteAsync(Common.Helpers.SongMediaPaths.FacebookImage(mediaGuid)),
            Times.Once);
    }

    [Test]
    public async Task InvalidateFacebookImageAsync_LegacyCoverArt_DeletesTheUnderscoreSuffixedImage()
    {
        await _service.InvalidateFacebookImageAsync("Night Drive/Night Drive.jpg");

        _mockStorageService.Verify(
            service => service.DeleteAsync("Night Drive/Night Drive_fb.jpg"),
            Times.Once);
    }

    [Test]
    public async Task InvalidateFacebookImageAsync_AlsoClearsTheSupersededPngSharingImage()
    {
        // Sharing images moved from PNG to JPEG. A song whose image was last generated before the
        // change still has a .png in storage, and nothing else will ever overwrite it.
        await _service.InvalidateFacebookImageAsync("Night Drive/Night Drive.jpg");

        _mockStorageService.Verify(
            service => service.DeleteAsync("Night Drive/Night Drive_fb.png"),
            Times.Once);
    }

    [Test]
    public async Task InvalidateFacebookImageAsync_GuidScheme_ClearsBothTheJpegAndTheSupersededPng()
    {
        var mediaGuid = Guid.NewGuid();
        var name = mediaGuid.ToString("N");

        await _service.InvalidateFacebookImageAsync(Common.Helpers.SongMediaPaths.CoverArt(mediaGuid, ".jpg"));

        _mockStorageService.Verify(service => service.DeleteAsync($"{name}/{name}-fb.jpg"), Times.Once);
        _mockStorageService.Verify(service => service.DeleteAsync($"{name}/{name}-fb.png"), Times.Once);
    }

    [Test]
    public async Task InvalidateFacebookImageAsync_WhenDeleteFails_DoesNotThrow()
    {
        _mockStorageService.Setup(service => service.DeleteAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("blob under lease"));

        Assert.DoesNotThrowAsync(() => _service.InvalidateFacebookImageAsync("folder/cover.jpg"));
        await Task.CompletedTask;
    }

    [TestCase("")]
    [TestCase(null)]
    public async Task InvalidateFacebookImageAsync_BlankPath_DeletesNothing(string coverArtPath)
    {
        await _service.InvalidateFacebookImageAsync(coverArtPath);

        _mockStorageService.Verify(service => service.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GetOrCreateFacebookImageAsync_WhenFbImageExists_ReturnsFbPath()
    {
        // Arrange
        var originalPath = "folder/image.jpg";
        var fbPath = "folder/image_fb.jpg";
        _mockStorageService.Setup(s => s.ExistsAsync(fbPath)).ReturnsAsync(true);

        // Act
        var result = await _service.GetOrCreateFacebookImageAsync(originalPath);

        // Assert
        Assert.That(result, Is.EqualTo(fbPath));
        _mockStorageService.Verify(s => s.DownloadAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GetOrCreateFacebookImageAsync_WhenOriginalStreamEmpty_ReturnsOriginalPath()
    {
        // Arrange
        var originalPath = "folder/image.jpg";
        _mockStorageService.Setup(s => s.ExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
        _mockStorageService.Setup(s => s.DownloadAsync(originalPath)).ReturnsAsync(new MemoryStream());

        // Act
        var result = await _service.GetOrCreateFacebookImageAsync(originalPath);

        // Assert
        Assert.That(result, Is.EqualTo(originalPath));
    }

    [Test]
    public async Task GetOrCreateFacebookImageAsync_WhenEmptyPath_ReturnsOriginalPath()
    {
        // Act
        var result = await _service.GetOrCreateFacebookImageAsync("");

        // Assert
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public async Task GenerateSongMetaTagsAsync_WithExistingFbImage_UsesFbImageUrl()
    {
        // Arrange
        var songTitle = "Test%20Song";
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "Test Song.mp3",
                ImageBlobPath = "Test Song/cover.jpg",
                AlbumName = null,
                Genre = "Rock"
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);
        _mockStorageService.Setup(s => s.ExistsAsync("Test Song/cover_fb.jpg")).ReturnsAsync(true);

        // Act
        var result = await _service.GenerateSongMetaTagsAsync(songTitle);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Contain("cover_fb.jpg"));
        Assert.That(result, Does.Contain("og:image:width"));
        Assert.That(result, Does.Contain("1200"));
        Assert.That(result, Does.Contain("og:image:height"));
        Assert.That(result, Does.Contain("630"));
    }

    [Test]
    public void CreateFacebookImage_WithValidImage_ReturnsCorrectDimensions()
    {
        // Arrange - create a simple 100x100 test image
        using var bitmap = new SkiaSharp.SKBitmap(100, 100);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.Red);

        using var imageStream = new MemoryStream();
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        data.SaveTo(imageStream);
        imageStream.Position = 0;

        // Act
        using var result = OpenGraphService.CreateFacebookImage(imageStream);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Length, Is.GreaterThan(0));

        // Verify the output dimensions
        result.Position = 0;
        using var outputBitmap = SkiaSharp.SKBitmap.Decode(result);
        Assert.That(outputBitmap.Width, Is.EqualTo(OpenGraphService.FacebookImageWidth));
        Assert.That(outputBitmap.Height, Is.EqualTo(OpenGraphService.FacebookImageHeight));
    }

    [Test]
    public void CreateFacebookImage_WithWideImage_CentersVertically()
    {
        // Arrange - create a wide 1200x400 test image (wider than tall)
        using var bitmap = new SkiaSharp.SKBitmap(1200, 400);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.Blue);

        using var imageStream = new MemoryStream();
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        data.SaveTo(imageStream);
        imageStream.Position = 0;

        // Act
        using var result = OpenGraphService.CreateFacebookImage(imageStream);

        // Assert
        Assert.That(result, Is.Not.Null);
        result.Position = 0;
        using var outputBitmap = SkiaSharp.SKBitmap.Decode(result);
        Assert.That(outputBitmap.Width, Is.EqualTo(1200));
        Assert.That(outputBitmap.Height, Is.EqualTo(630));

        // The top and bottom should have black padding (center pixel of top row should be black)
        var topPixel = outputBitmap.GetPixel(600, 0);
        Assert.That(topPixel, Is.EqualTo(SkiaSharp.SKColors.Black));
    }

    #region PreGenerateFacebookImageAsync Tests

    [Test]
    public async Task PreGenerateFacebookImageAsync_WithValidPath_CallsGetOrCreate()
    {
        // Arrange
        var imagePath = "folder/cover.jpg";
        _mockStorageService.Setup(s => s.ExistsAsync("folder/cover_fb.jpg")).ReturnsAsync(true);

        // Act
        await _service.PreGenerateFacebookImageAsync(imagePath);

        // Assert - should check if FB image exists
        _mockStorageService.Verify(s => s.ExistsAsync("folder/cover_fb.jpg"), Times.Once);
    }

    [Test]
    public async Task PreGenerateFacebookImageAsync_WithNullPath_DoesNotThrow()
    {
        // Act & Assert - should not throw
        await _service.PreGenerateFacebookImageAsync(null);
        _mockStorageService.Verify(s => s.ExistsAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task PreGenerateFacebookImageAsync_WithEmptyPath_DoesNotThrow()
    {
        // Act & Assert - should not throw
        await _service.PreGenerateFacebookImageAsync(string.Empty);
        _mockStorageService.Verify(s => s.ExistsAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task PreGenerateFacebookImageAsync_WhenStorageThrows_DoesNotThrow()
    {
        // Arrange
        var imagePath = "folder/cover.jpg";
        _mockStorageService.Setup(s => s.ExistsAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Storage error"));

        // Act & Assert - should swallow the exception
        Assert.DoesNotThrowAsync(() => _service.PreGenerateFacebookImageAsync(imagePath));
    }

    #endregion
}
