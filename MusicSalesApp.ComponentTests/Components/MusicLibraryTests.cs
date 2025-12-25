using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class MusicLibraryTests : BUnitTestBase
{
    private Mock<IJSRuntime> _mockJsRuntime;
    private Mock<IJSObjectReference> _mockJsModule;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        _mockJsRuntime = new Mock<IJSRuntime>();
        _mockJsModule = new Mock<IJSObjectReference>();

        // Mock JS module import
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(
                "import",
                It.IsAny<object[]>()))
            .ReturnsAsync(_mockJsModule.Object);

        TestContext.Services.AddSingleton<IJSRuntime>(_mockJsRuntime.Object);
        
        // Setup default HTTP client with stub handler
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        
        // Setup default responses for API endpoints that may be called
        handler.SetupJsonResponse(new Uri("http://localhost/api/cart/owned"), Array.Empty<string>());
        handler.SetupJsonResponse(new Uri("http://localhost/api/cart"), new { Items = Array.Empty<object>(), Albums = Array.Empty<object>(), Total = 0 });
        
        TestContext.Services.AddSingleton<HttpClient>(httpClient);
    }

    [Test]
    public void MusicLibrary_HasCorrectTitle()
    {
        // Arrange - Set up empty metadata list
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        var cut = TestContext.Render<MusicLibrary>();

        // Assert
        Assert.That(cut.Find("h3").TextContent, Is.EqualTo("Music Library"));
    }

    [Test]
    public void MusicLibrary_HasCardsGrid()
    {
        // Arrange - Set up empty metadata list
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have cards grid container
        Assert.That(cut.Markup, Does.Contain("music-cards-grid"));
    }

    [Test]
    public void MusicLibrary_DisplaysSongCards_WhenFilesExist()
    {
        // Arrange - Set up metadata with a matching song
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata 
            { 
                Mp3BlobPath = "TestSong.mp3",
                SongPrice = 0.99m,
                UpdatedAt = DateTime.Now
            }
        };
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have song cards
        Assert.That(cut.Markup, Does.Contain("music-card"));
        Assert.That(cut.Markup, Does.Contain("card-song-title"));
        Assert.That(cut.Markup, Does.Contain("TestSong"));
    }

    [Test]
    public void MusicLibrary_HasPlayAndViewButtons_ForEachCard()
    {
        // Arrange - Set up metadata with a matching song
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata 
            { 
                Mp3BlobPath = "TestSong.mp3",
                SongPrice = 0.99m,
                UpdatedAt = DateTime.Now
            }
        };
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have card actions div with play and view buttons
        Assert.That(cut.Markup, Does.Contain("card-actions"));
        Assert.That(cut.Markup, Does.Contain("title=\"play\""));
        Assert.That(cut.Markup, Does.Contain("title=\"view\""));
    }

    [Test]
    public void MusicLibrary_HasAlbumArtPlaceholder_WhenNoArtAvailable()
    {
        // Arrange - Set up metadata with a matching song but no image
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata 
            { 
                Mp3BlobPath = "TestSong.mp3",
                SongPrice = 0.99m,
                UpdatedAt = DateTime.Now
            }
        };
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have album art placeholder
        Assert.That(cut.Markup, Does.Contain("card-album-art-placeholder"));
    }

    [Test]
    public void MusicLibrary_HasViewButtonWithOnClickEvent()
    {
        // Arrange - Set up metadata with a matching song
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata 
            { 
                Mp3BlobPath = "TestSong.mp3",
                SongPrice = 0.99m,
                UpdatedAt = DateTime.Now
            }
        };
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have button with view title and blazor onclick attribute
        // The GetSongPlayerUrl method navigates to /song/{title} when clicked
        var viewButtons = cut.FindAll("button[title='view']");
        Assert.That(viewButtons.Count, Is.GreaterThan(0));
    }

    [Test]
    public void MusicLibrary_DisplaysSongPriceFromMetadata()
    {
        // Arrange - authorize user so cart button with price is visible
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");
        
        // Set up metadata with the expected song price
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>
            {
                new MusicSalesApp.Models.SongMetadata { Mp3BlobPath = "TestSong.mp3", SongPrice = 2.49m }
            });

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should display price from metadata
        Assert.That(cut.Markup, Does.Contain("$2.49"));
    }

    [Test]
    public void MusicLibrary_DisplaysAlbumPriceFromMetadata()
    {
        // Arrange - authorize user so cart button with price is visible
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");
        
        // Set up metadata with the expected album price
        // The component looks for IsAlbumCover=true entries and then finds matching tracks by AlbumName
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>
            {
                // Album cover entry with price
                new MusicSalesApp.Models.SongMetadata 
                { 
                    BlobPath = "AlbumCover.jpg", 
                    ImageBlobPath = "AlbumCover.jpg",
                    IsAlbumCover = true, 
                    AlbumName = "TestAlbum",
                    AlbumPrice = 12.99m 
                },
                // Track entry that belongs to the album
                new MusicSalesApp.Models.SongMetadata 
                { 
                    BlobPath = "Track1.mp3",
                    Mp3BlobPath = "Track1.mp3", 
                    AlbumName = "TestAlbum"
                }
            });

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should display album price from metadata
        Assert.That(cut.Markup, Does.Contain("$12.99"));
    }

    private new class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, HttpResponseMessage> _responses = new();

        public void SetupJsonResponse(Uri uri, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            _responses[uri] = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.TryGetValue(request.RequestUri, out var response))
            {
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
