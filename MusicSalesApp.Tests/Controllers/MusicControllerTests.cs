using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Controllers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Text;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class MusicControllerTests
{
    private Mock<IAzureStorageService> _mockStorageService;
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IStreamCountService> _mockStreamCountService;
    private Mock<ISongMetadataService> _mockSongMetadataService;
    private Mock<ISongLikeService> _mockSongLikeService;
    private Mock<ICreatorPersonaService> _mockCreatorPersonaService;
    private Mock<IReportedSongService> _mockReportedSongService;
    private IMobileSongMapper _songMapper;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<ILogger<MusicController>> _mockLogger;
    private MusicController _controller;

    [SetUp]
    public void Setup()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockStreamCountService = new Mock<IStreamCountService>();
        _mockSongMetadataService = new Mock<ISongMetadataService>();
        _mockSongLikeService = new Mock<ISongLikeService>();
        _mockCreatorPersonaService = new Mock<ICreatorPersonaService>();
        _mockReportedSongService = new Mock<IReportedSongService>();
        _songMapper = new MobileSongMapper(_mockStorageService.Object, _mockCreatorPersonaService.Object);
        _mockLogger = new Mock<ILogger<MusicController>>();
        
        // Mock UserManager with required dependencies
        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null, null, null, null, null, null, null, null);
        
        _controller = new MusicController(
            _mockStorageService.Object,
            _mockSubscriptionService.Object,
            _mockStreamCountService.Object,
            _mockSongMetadataService.Object,
            _mockSongLikeService.Object,
            _mockCreatorPersonaService.Object,
            _mockReportedSongService.Object,
            _songMapper,
            _mockUserManager.Object,
            _mockLogger.Object);

        // Set up HttpContext for controller (required for Response.Headers access)
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }
    
    [Test]
    public async Task Stream_WithValidFile_ReturnsFileResult()
    {
        // Arrange
        var fileName = "test.mp3";
        var fileInfo = new StorageFileInfo
        {
            Name = fileName,
            Length = 1000,
            ContentType = "audio/mpeg"
        };
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));

        _mockStorageService.Setup(s => s.GetFileInfoAsync(fileName)).ReturnsAsync(fileInfo);
        _mockStorageService.Setup(s => s.OpenReadAsync(fileName)).ReturnsAsync(stream);

        // Act
        var result = await _controller.Stream(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<FileStreamResult>());
    }

    [Test]
    public async Task Stream_WithEmptyFileName_ReturnsBadRequest()
    {
        // Arrange
        var fileName = string.Empty;

        // Act
        var result = await _controller.Stream(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task Stream_WithNonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var fileName = "nonexistent.mp3";
        _mockStorageService.Setup(s => s.OpenReadAsync(fileName)).ReturnsAsync((Stream)null);

        // Act
        var result = await _controller.Stream(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetStreamUrl_WithValidFile_ReturnsOkWithSasUrl()
    {
        // Arrange
        var fileName = "test.mp3";
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sv=2021-06-08&st=2024-01-01T00%3A00%3A00Z&se=2024-01-02T00%3A00%3A00Z&sr=b&sp=r&sig=signature");
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null);
        _mockStorageService.Setup(s => s.GetReadSasUri(fileName, It.IsAny<TimeSpan>()))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrl(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        var value = okResult.Value;
        var urlProperty = value.GetType().GetProperty("url");
        var url = urlProperty.GetValue(value) as string;
        Assert.That(url, Is.EqualTo(sasUri.ToString()));
    }

    [Test]
    public async Task GetStreamUrl_WithEmptyFileName_ReturnsBadRequest()
    {
        // Arrange
        var fileName = string.Empty;

        // Act
        var result = await _controller.GetStreamUrl(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task GetStreamUrl_ForSubscriber_UsesLongerLifetime()
    {
        // Arrange
        var fileName = "test.mp3";
        var userId = 123;
        var user = new ApplicationUser { Id = userId, UserName = "testuser" };
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sv=2021-06-08&st=2024-01-01T00%3A00%3A00Z&se=2024-01-02T00%3A00%3A00Z&sr=b&sp=r&sig=signature");
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockSubscriptionService.Setup(s => s.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(true);
        _mockStorageService.Setup(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(24)))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrl(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStorageService.Verify(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(24)), Times.Once);
    }

    [Test]
    public async Task GetStreamUrl_ForNonSubscriber_UsesShorterLifetime()
    {
        // Arrange
        var fileName = "test.mp3";
        var userId = 123;
        var user = new ApplicationUser { Id = userId, UserName = "testuser" };
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sv=2021-06-08&st=2024-01-01T00%3A00%3A00Z&se=2024-01-02T00%3A00%3A00Z&sr=b&sp=r&sig=signature");
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockSubscriptionService.Setup(s => s.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(false);
        _mockStorageService.Setup(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(2)))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrl(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStorageService.Verify(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(2)), Times.Once);
    }

    [Test]
    public async Task RecordStream_WithValidId_ReturnsOkWithStreamCount()
    {
        // Arrange
        var songMetadataId = 1;
        var newCount = 42;
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null);
        _mockStreamCountService.Setup(s => s.IncrementStreamCountAsync(songMetadataId, null, false))
            .ReturnsAsync(newCount);

        // Act
        var result = await _controller.RecordStream(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        var value = okResult.Value;
        var streamCountProperty = value.GetType().GetProperty("streamCount");
        var streamCount = (int)streamCountProperty.GetValue(value);
        Assert.That(streamCount, Is.EqualTo(newCount));
    }

    [Test]
    public async Task RecordStream_WithAdminUser_PassesIsAdminTrue()
    {
        // Arrange
        var songMetadataId = 1;
        var adminUser = new ApplicationUser { Id = 1, UserName = "admin@app.com" };
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(adminUser);
        _mockUserManager.Setup(x => x.IsInRoleAsync(adminUser, "Admin"))
            .ReturnsAsync(true);
        _mockStreamCountService.Setup(s => s.IncrementStreamCountAsync(songMetadataId, adminUser.Id, true))
            .ReturnsAsync(10);

        // Act
        var result = await _controller.RecordStream(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStreamCountService.Verify(s => s.IncrementStreamCountAsync(songMetadataId, adminUser.Id, true), Times.Once);
    }

    [Test]
    public async Task RecordStream_WithRegularUser_PassesIsAdminFalse()
    {
        // Arrange
        var songMetadataId = 1;
        var regularUser = new ApplicationUser { Id = 2, UserName = "user@app.com" };
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(regularUser);
        _mockUserManager.Setup(x => x.IsInRoleAsync(regularUser, "Admin"))
            .ReturnsAsync(false);
        _mockStreamCountService.Setup(s => s.IncrementStreamCountAsync(songMetadataId, regularUser.Id, false))
            .ReturnsAsync(15);

        // Act
        var result = await _controller.RecordStream(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStreamCountService.Verify(s => s.IncrementStreamCountAsync(songMetadataId, regularUser.Id, false), Times.Once);
    }

    [Test]
    public async Task RecordStream_WithInvalidId_ReturnsBadRequest()
    {
        // Arrange
        var songMetadataId = 0;

        // Act
        var result = await _controller.RecordStream(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetStreamCount_WithValidId_ReturnsOkWithCount()
    {
        // Arrange
        var songMetadataId = 1;
        var count = 100;
        
        _mockStreamCountService.Setup(s => s.GetStreamCountAsync(songMetadataId))
            .ReturnsAsync(count);

        // Act
        var result = await _controller.GetStreamCount(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        var value = okResult.Value;
        var streamCountProperty = value.GetType().GetProperty("streamCount");
        var streamCount = (int)streamCountProperty.GetValue(value);
        Assert.That(streamCount, Is.EqualTo(count));
    }

    [Test]
    public async Task GetStreamCount_WithInvalidId_ReturnsBadRequest()
    {
        // Arrange
        var songMetadataId = -1;

        // Act
        var result = await _controller.GetStreamCount(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetSongs_ReturnsAllActiveSongsWithSasUrls()
    {
        // Arrange
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/file?sig=test");
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                SongTitle = "Test Song",
                ArtistName = "Test Artist",
                Genre = "Rock",
                Mp3BlobPath = "folder/test.mp3",
                ImageBlobPath = "folder/test.jpg",
                NumberOfStreams = 42,
                TrackLength = 180.5,
                DisplayOnHomePage = true,
                CreatorId = 77,
                Creator = new Creator { Id = 77, UserId = 88 }
            },
            new SongMetadata
            {
                Id = 2,
                SongTitle = "Another Song",
                Genre = "Pop",
                Mp3BlobPath = "folder/another.mp3",
                NumberOfStreams = 10,
                TrackLength = 200.0
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetSongs();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var songs = okResult.Value as List<SongListItemDto>;
        Assert.That(songs, Is.Not.Null);
        Assert.That(songs, Has.Count.EqualTo(2));
        Assert.That(songs[0].SongTitle, Is.EqualTo("Test Song"));
        Assert.That(songs[0].ArtistName, Is.EqualTo("Test Artist"));
        Assert.That(songs[0].Genre, Is.EqualTo("Rock"));
        Assert.That(songs[0].StreamCount, Is.EqualTo(42));
        Assert.That(songs[0].TrackLengthSeconds, Is.EqualTo(180.5));
        Assert.That(songs[0].DisplayOnHomePage, Is.True);
        Assert.That(songs[0].CreatorId, Is.EqualTo(77));
        Assert.That(songs[0].CreatorUserId, Is.EqualTo(88));
        Assert.That(songs[0].AlbumArtUrl, Is.EqualTo(sasUri.ToString()));
        Assert.That(songs[0].StreamUrl, Is.EqualTo(sasUri.ToString()));
    }

    [Test]
    public async Task GetSongs_ExcludesEntriesWithoutMp3BlobPath()
    {
        // Arrange - include an image-only entry (album cover)
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/file?sig=test");
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "folder/song.mp3",
                ImageBlobPath = "folder/song.jpg",
                SongTitle = "Real Song"
            },
            new SongMetadata
            {
                Id = 2,
                ImageBlobPath = "folder/cover.jpg",
                IsAlbumCover = true
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetSongs();

        // Assert
        var okResult = (OkObjectResult)result;
        var songs = okResult.Value as List<SongListItemDto>;
        Assert.That(songs, Has.Count.EqualTo(1));
        Assert.That(songs[0].SongTitle, Is.EqualTo("Real Song"));
    }

    [Test]
    public async Task GetSongs_FallsBackToFileNameWhenNoSongTitle()
    {
        // Arrange
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/file?sig=test");
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "folder/My Great Song.mp3",
                SongTitle = null
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetSongs();

        // Assert
        var okResult = (OkObjectResult)result;
        var songs = okResult.Value as List<SongListItemDto>;
        Assert.That(songs[0].SongTitle, Is.EqualTo("My Great Song"));
    }

    [Test]
    public async Task GetSongs_ReturnsNullAlbumArtUrlWhenNoImageBlobPath()
    {
        // Arrange
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/file?sig=test");
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "folder/song.mp3",
                ImageBlobPath = null,
                SongTitle = "No Art Song"
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetSongs();

        // Assert
        var okResult = (OkObjectResult)result;
        var songs = okResult.Value as List<SongListItemDto>;
        Assert.That(songs[0].AlbumArtUrl, Is.Null);
    }

    [Test]
    public async Task GetSongs_ReturnsEmptyListWhenNoSongs()
    {
        // Arrange
        _mockSongMetadataService.Setup(s => s.GetAllAsync())
            .ReturnsAsync(new List<SongMetadata>());

        // Act
        var result = await _controller.GetSongs();

        // Assert
        var okResult = (OkObjectResult)result;
        var songs = okResult.Value as List<SongListItemDto>;
        Assert.That(songs, Is.Empty);
    }

    [Test]
    public async Task GetSongs_IncludesPersonaImageUrlWhenPersonaHasImage()
    {
        // Arrange
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/file?sig=test");
        var personaSasUrl = "https://storage.blob.core.windows.net/persona/image.jpg?sig=test";
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                SongTitle = "Persona Song",
                Mp3BlobPath = "folder/song.mp3",
                ImageBlobPath = "folder/cover.jpg",
                PersonaId = 1,
                Persona = new CreatorPersona
                {
                    Id = 1,
                    Name = "Stage Name",
                    ImageBlobPath = "personas/avatar.jpg",
                    IsEnabled = true
                }
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(sasUri);
        _mockCreatorPersonaService
            .Setup(s => s.GetPersonaImageSasUrl("personas/avatar.jpg", It.IsAny<TimeSpan>()))
            .Returns(personaSasUrl);

        // Act
        var result = await _controller.GetSongs();

        // Assert
        var okResult = (OkObjectResult)result;
        var songs = okResult.Value as List<SongListItemDto>;
        Assert.That(songs, Has.Count.EqualTo(1));
        Assert.That(songs[0].PersonaImageUrl, Is.EqualTo(personaSasUrl));
    }

    [Test]
    public async Task GetSongs_ReturnsNullPersonaImageUrlWhenNoPersona()
    {
        // Arrange
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/file?sig=test");
        var metadata = new List<SongMetadata>
        {
            new SongMetadata
            {
                Id = 1,
                SongTitle = "No Persona Song",
                Mp3BlobPath = "folder/song.mp3"
            }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync(metadata);
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetSongs();

        // Assert
        var okResult = (OkObjectResult)result;
        var songs = okResult.Value as List<SongListItemDto>;
        Assert.That(songs, Has.Count.EqualTo(1));
        Assert.That(songs[0].PersonaImageUrl, Is.Null);
    }

    [Test]
    public async Task GetSongByTitle_IncludesCreatorIdentifiers()
    {
        // Arrange
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/file?sig=test");
        var song = new SongMetadata
        {
            Id = 5,
            SongTitle = "Deep Link Song",
            ArtistName = "Artist",
            Genre = "Rock",
            Mp3BlobPath = "folder/deep-link.mp3",
            ImageBlobPath = "folder/deep-link.jpg",
            TrackLength = 123.4,
            CreatorId = 55,
            Creator = new Creator { Id = 55, UserId = 99 }
        };

        _mockSongMetadataService.Setup(s => s.GetAllAsync()).ReturnsAsync([song]);
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(sasUri);
        _mockStreamCountService.Setup(s => s.GetStreamCountAsync(song.Id)).ReturnsAsync(321);
        _mockSongLikeService.Setup(s => s.GetLikeCountsAsync(song.Id)).ReturnsAsync((7, 2));

        // Act
        var result = await _controller.GetSongByTitle("Deep%20Link%20Song");

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.Value, Is.Not.Null);

        var creatorId = okResult.Value!.GetType().GetProperty("creatorId")!.GetValue(okResult.Value);
        var creatorUserId = okResult.Value.GetType().GetProperty("creatorUserId")!.GetValue(okResult.Value);
        var streamCount = okResult.Value.GetType().GetProperty("streamCount")!.GetValue(okResult.Value);

        Assert.That(creatorId, Is.EqualTo(55));
        Assert.That(creatorUserId, Is.EqualTo(99));
        Assert.That(streamCount, Is.EqualTo(321));
    }

    // --- GetBulkLikeCounts tests ---

    [Test]
    public async Task GetBulkLikeCounts_WithValidIds_ReturnsLikeDislikeCounts()
    {
        // Arrange
        var counts = new Dictionary<int, (int likeCount, int dislikeCount)>
        {
            { 1, (5, 2) },
            { 2, (10, 3) }
        };
        _mockSongLikeService.Setup(s => s.GetBulkLikeDislikeCountsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(counts);

        // Act
        var result = await _controller.GetBulkLikeCounts("1,2");

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var items = (okResult.Value as System.Collections.IEnumerable)!.Cast<object>().ToList();
        Assert.That(items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetBulkLikeCounts_WithEmptyIds_ReturnsEmptyArray()
    {
        // Act
        var result = await _controller.GetBulkLikeCounts("");

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var items = (okResult.Value as System.Collections.IEnumerable)!.Cast<object>().ToList();
        Assert.That(items, Is.Empty);
    }

    [Test]
    public async Task GetBulkLikeCounts_WithInvalidIds_ReturnsEmptyArray()
    {
        // Act
        var result = await _controller.GetBulkLikeCounts("abc,xyz");

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var items = (okResult.Value as System.Collections.IEnumerable)!.Cast<object>().ToList();
        Assert.That(items, Is.Empty);
    }

    [Test]
    public async Task GetBulkLikeCounts_ReturnZeroForMissingIds()
    {
        // Arrange - id 3 is not in the dictionary so should get 0/0
        var counts = new Dictionary<int, (int likeCount, int dislikeCount)>
        {
            { 1, (5, 2) }
        };
        _mockSongLikeService.Setup(s => s.GetBulkLikeDislikeCountsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(counts);

        // Act
        var result = await _controller.GetBulkLikeCounts("1,3");

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var items = (okResult.Value as System.Collections.IEnumerable)!.Cast<object>().ToList();
        Assert.That(items, Has.Count.EqualTo(2));
        // Item for id=3 should have 0 counts via TryGetValue default
        var item3 = items[1];
        var likeCountProp = item3.GetType().GetProperty("likeCount");
        Assert.That((int)likeCountProp!.GetValue(item3)!, Is.EqualTo(0));
    }

    // --- ToggleLike tests ---

    [Test]
    public async Task ToggleLike_WithValidIdAndAuthUser_ReturnsOk()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "testuser" };
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockSongLikeService.Setup(s => s.ToggleLikeAsync(user.Id, 42))
            .ReturnsAsync(true);
        _mockSongLikeService.Setup(s => s.GetLikeCountsAsync(42))
            .ReturnsAsync((10, 3));

        // Act
        var result = await _controller.ToggleLike(42);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var value = okResult.Value!;
        Assert.That((bool)value.GetType().GetProperty("isLiked")!.GetValue(value)!, Is.True);
        Assert.That((int)value.GetType().GetProperty("likeCount")!.GetValue(value)!, Is.EqualTo(10));
        Assert.That((int)value.GetType().GetProperty("dislikeCount")!.GetValue(value)!, Is.EqualTo(3));
    }

    [Test]
    public async Task ToggleLike_WithInvalidId_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.ToggleLike(0);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ToggleLike_WithNoUser_ReturnsUnauthorized()
    {
        // Arrange
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null);

        // Act
        var result = await _controller.ToggleLike(42);

        // Assert
        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    // --- ToggleDislike tests ---

    [Test]
    public async Task ToggleDislike_WithValidIdAndAuthUser_ReturnsOk()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "testuser" };
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockSongLikeService.Setup(s => s.ToggleDislikeAsync(user.Id, 42))
            .ReturnsAsync(true);
        _mockSongLikeService.Setup(s => s.GetLikeCountsAsync(42))
            .ReturnsAsync((5, 7));

        // Act
        var result = await _controller.ToggleDislike(42);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var value = okResult.Value!;
        Assert.That((bool)value.GetType().GetProperty("isDisliked")!.GetValue(value)!, Is.True);
        Assert.That((int)value.GetType().GetProperty("likeCount")!.GetValue(value)!, Is.EqualTo(5));
        Assert.That((int)value.GetType().GetProperty("dislikeCount")!.GetValue(value)!, Is.EqualTo(7));
    }

    [Test]
    public async Task ToggleDislike_WithInvalidId_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.ToggleDislike(0);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ToggleDislike_WithNoUser_ReturnsUnauthorized()
    {
        // Arrange
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null);

        // Act
        var result = await _controller.ToggleDislike(42);

        // Assert
        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    // --- ReportSong Tests ---

    [Test]
    public async Task ReportSong_WithValidRequest_ReturnsOk()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "testuser" };
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockReportedSongService.Setup(s => s.ReportSongAsync(1, 42, "Copyright Violation"))
            .ReturnsAsync(new ReportedSong { Id = 1 });

        var request = new MusicController.ReportSongRequest { Reason = "Copyright Violation" };

        // Act
        var result = await _controller.ReportSong(42, request);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockReportedSongService.Verify(s => s.ReportSongAsync(1, 42, "Copyright Violation"), Times.Once);
    }

    [Test]
    public async Task ReportSong_WithInvalidSongId_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.ReportSong(0, new MusicController.ReportSongRequest { Reason = "Copyright Violation" });

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ReportSong_WithEmptyReason_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.ReportSong(42, new MusicController.ReportSongRequest { Reason = "" });

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ReportSong_WithInvalidReason_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.ReportSong(42, new MusicController.ReportSongRequest { Reason = "Invalid Reason" });

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ReportSong_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null);

        var request = new MusicController.ReportSongRequest { Reason = "Copyright Violation" };

        // Act
        var result = await _controller.ReportSong(42, request);

        // Assert
        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task ReportSong_WhenSongNotFound_ReturnsNotFound()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "testuser" };
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockReportedSongService.Setup(s => s.ReportSongAsync(1, 999, "Copyright Violation"))
            .ThrowsAsync(new InvalidOperationException("Song with ID 999 not found."));

        var request = new MusicController.ReportSongRequest { Reason = "Copyright Violation" };

        // Act
        var result = await _controller.ReportSong(999, request);

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task ReportSong_WhenAlreadyReported_ReturnsConflict()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "testuser" };
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockReportedSongService.Setup(s => s.ReportSongAsync(1, 42, "Copyright Violation"))
            .ThrowsAsync(new InvalidOperationException("You have already reported this song."));

        var request = new MusicController.ReportSongRequest { Reason = "Copyright Violation" };

        // Act
        var result = await _controller.ReportSong(42, request);

        // Assert
        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
    }
}
