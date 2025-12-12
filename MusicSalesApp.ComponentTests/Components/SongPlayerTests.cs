using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Services;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class SongPlayerTests : BUnitTestBase
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
        
        // Setup default responses for API endpoints
        handler.SetupJsonResponse(new Uri("http://localhost/api/cart/status/TestSong.mp3"), 
            new { Owns = false, InCart = false });
        handler.SetupJsonResponse(new Uri("http://localhost/api/music/url/TestSong.mp3"), 
            new { Url = "http://localhost/api/music/TestSong.mp3" });
            
        TestContext.Services.AddSingleton<HttpClient>(httpClient);
    }

    [Test]
    public void SongPlayer_DisplaysLoadingState_Initially()
    {
        // Arrange - Set up empty metadata list
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // After initial render with empty results, should show error
        Assert.That(cut.Markup, Does.Contain("not found"));
    }

    [Test]
    public void SongPlayer_ShowsErrorMessage_WhenNoSongTitleProvided()
    {
        // Act
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, ""));

        // Assert
        Assert.That(cut.Markup, Does.Contain("No song title provided"));
    }

    [Test]
    public void SongPlayer_ShowsSongNotFound_WhenSongDoesNotExist()
    {
        // Arrange - Set up empty metadata list
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "NonExistentSong"));

        // Assert
        Assert.That(cut.Markup, Does.Contain("not found"));
    }

    [Test]
    public void SongPlayer_DisplaysSongTitle_WhenSongExists()
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
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // Assert - should display the song title
        Assert.That(cut.Markup, Does.Contain("TestSong"));
    }

    [Test]
    public void SongPlayer_HasPlayButton_WhenSongLoaded()
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
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // Assert - should have play buttons
        var playButtons = cut.FindAll("button.play-button-large");
        Assert.That(playButtons.Count, Is.GreaterThan(0));
    }

    [Test]
    public void SongPlayer_HasPlayerControls_WhenSongLoaded()
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
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // Assert - should have player bar with controls
        Assert.That(cut.Markup, Does.Contain("player-bar"));
        Assert.That(cut.Markup, Does.Contain("player-controls"));
        Assert.That(cut.Markup, Does.Contain("progress-container"));
    }

    [Test]
    public void SongPlayer_DisplaysTimeFormat_Correctly()
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
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // Assert - should display time format (0:00)
        Assert.That(cut.Markup, Does.Contain("0:00"));
    }

    [Test]
    public void SongPlayer_DisplaysSongPriceFromMetadata()
    {
        // Arrange
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");
        
        // Set up metadata with the expected song price
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata 
            { 
                Mp3BlobPath = "TestSong.mp3",
                SongPrice = 1.99m,
                UpdatedAt = DateTime.Now
            }
        };
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // Assert - should display price from metadata
        Assert.That(cut.Markup, Does.Contain("$1.99"));
    }

    [Test]
    public void SongPlayer_DisplaysDefaultPriceWhenTagMissing()
    {
        // Arrange
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        // Set up metadata without a song price
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata 
            { 
                Mp3BlobPath = "TestSong.mp3",
                SongPrice = null, // No price set
                UpdatedAt = DateTime.Now
            }
        };
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // Assert - should display default price
        Assert.That(cut.Markup, Does.Contain("$0.99"));
    }

    [Test]
    public void SongPlayer_DisplaysTrackLengthFromMetadata()
    {
        // Arrange
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");
        
        // Set up metadata with the expected track length (245.67 seconds = 4:05)
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata 
            { 
                Mp3BlobPath = "TestSong.mp3",
                SongPrice = 0.99m,
                TrackLength = 245.67,
                UpdatedAt = DateTime.Now
            }
        };
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // Assert - should display track length (245.67 seconds = 4:05)
        Assert.That(cut.Markup, Does.Contain("4:05"));
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
