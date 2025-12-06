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

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        _mockJsRuntime = new Mock<IJSRuntime>();

        // Mock JS module import
        var mockJsModule = new Mock<IJSObjectReference>();
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(
                "import",
                It.IsAny<object[]>()))
            .ReturnsAsync(mockJsModule.Object);

        TestContext.Services.AddSingleton<IJSRuntime>(_mockJsRuntime.Object);
    }

    [Test]
    public void SongPlayer_DisplaysLoadingState_Initially()
    {
        // Arrange - no song data set up, so it will show loading initially
        var handler = new StubHttpMessageHandler();
        // Set up empty response to simulate loading
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), Array.Empty<object>());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

        // Act
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // After initial render with empty results, should show error
        Assert.That(cut.Markup, Does.Contain("not found").Or.Contain("Loading"));
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
        // Arrange
        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), Array.Empty<object>());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

        // Act
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "NonExistentSong"));

        // Assert
        Assert.That(cut.Markup, Does.Contain("not found"));
    }

    [Test]
    public void SongPlayer_DisplaysSongTitle_WhenSongExists()
    {
        // Arrange
        var files = new[]
        {
            new { Name = "TestSong.mp3", Length = 1024L, ContentType = "audio/mpeg", LastModified = DateTimeOffset.Now }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

        // Act
        var cut = TestContext.Render<SongPlayer>(
            pb => pb.Add(p => p.SongTitle, "TestSong"));

        // Assert - should display the song title
        Assert.That(cut.Markup, Does.Contain("TestSong"));
    }

    [Test]
    public void SongPlayer_HasPlayButton_WhenSongLoaded()
    {
        // Arrange
        var files = new[]
        {
            new { Name = "TestSong.mp3", Length = 1024L, ContentType = "audio/mpeg", LastModified = DateTimeOffset.Now }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

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
        // Arrange
        var files = new[]
        {
            new { Name = "TestSong.mp3", Length = 1024L, ContentType = "audio/mpeg", LastModified = DateTimeOffset.Now }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

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
        // Arrange
        var files = new[]
        {
            new { Name = "TestSong.mp3", Length = 1024L, ContentType = "audio/mpeg", LastModified = DateTimeOffset.Now }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

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
        MockSongMetadataService.Setup(x => x.GetByBlobPathAsync("TestSong.mp3"))
            .ReturnsAsync(new MusicSalesApp.Models.SongMetadata { Mp3BlobPath = "TestSong.mp3", SongPrice = 1.99m });

        var files = new[]
        {
            new 
            { 
                Name = "TestSong.mp3", 
                Length = 1024L, 
                ContentType = "audio/mpeg", 
                LastModified = DateTimeOffset.Now,
                Tags = new Dictionary<string, string>()
            }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

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

        var files = new[]
        {
            new 
            { 
                Name = "TestSong.mp3", 
                Length = 1024L, 
                ContentType = "audio/mpeg", 
                LastModified = DateTimeOffset.Now,
                Tags = new Dictionary<string, string>()
            }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

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
        MockSongMetadataService.Setup(x => x.GetByBlobPathAsync("TestSong.mp3"))
            .ReturnsAsync(new MusicSalesApp.Models.SongMetadata { Mp3BlobPath = "TestSong.mp3", TrackLength = 245.67 });

        var files = new[]
        {
            new 
            { 
                Name = "TestSong.mp3", 
                Length = 1024L, 
                ContentType = "audio/mpeg", 
                LastModified = DateTimeOffset.Now,
                Tags = new Dictionary<string, string>()
            }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

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
