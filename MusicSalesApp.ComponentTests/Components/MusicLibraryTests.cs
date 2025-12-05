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
    public void MusicLibrary_HasCorrectTitle()
    {
        // Act
        var cut = TestContext.Render<MusicLibrary>();

        // Assert
        Assert.That(cut.Find("h3").TextContent, Is.EqualTo("Music Library"));
    }

    [Test]
    public void MusicLibrary_HasCardsGrid()
    {
        // Act
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have cards grid container
        Assert.That(cut.Markup, Does.Contain("music-cards-grid"));
        Assert.That(cut.Markup, Does.Contain("music-library-container"));
    }

    [Test]
    public void MusicLibrary_DisplaysSongCards_WhenFilesExist()
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
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have song cards
        Assert.That(cut.Markup, Does.Contain("music-card"));
        Assert.That(cut.Markup, Does.Contain("card-song-title"));
        Assert.That(cut.Markup, Does.Contain("TestSong"));
    }

    [Test]
    public void MusicLibrary_HasPlayAndViewButtons_ForEachCard()
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
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have play button and view button
        Assert.That(cut.Markup, Does.Contain("card-play-button"));
        Assert.That(cut.Markup, Does.Contain("card-view-button"));
    }

    [Test]
    public void MusicLibrary_HasAlbumArtPlaceholder_WhenNoArtAvailable()
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
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have album art placeholder
        Assert.That(cut.Markup, Does.Contain("card-album-art-placeholder"));
    }

    [Test]
    public void MusicLibrary_HasViewLinkToSongPlayer()
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
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have link to song player
        Assert.That(cut.Markup, Does.Contain("/song/TestSong"));
    }

    [Test]
    public void MusicLibrary_DisplaysSongPriceFromIndexTag()
    {
        // Arrange
        var files = new[]
        {
            new 
            { 
                Name = "TestSong.mp3", 
                Length = 1024L, 
                ContentType = "audio/mpeg", 
                LastModified = DateTimeOffset.Now,
                Tags = new Dictionary<string, string> 
                { 
                    { "SongPrice", "2.49" } 
                }
            }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

        // Act
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should display price from index tag
        Assert.That(cut.Markup, Does.Contain("$2.49"));
    }

    [Test]
    public void MusicLibrary_DisplaysAlbumPriceFromIndexTag()
    {
        // Arrange
        var files = new[]
        {
            new 
            { 
                Name = "AlbumCover.jpg", 
                Length = 2048L, 
                ContentType = "image/jpeg", 
                LastModified = DateTimeOffset.Now,
                Tags = new Dictionary<string, string> 
                { 
                    { "IsAlbumCover", "true" },
                    { "AlbumName", "TestAlbum" },
                    { "AlbumPrice", "12.99" }
                }
            },
            new 
            { 
                Name = "Track1.mp3", 
                Length = 1024L, 
                ContentType = "audio/mpeg", 
                LastModified = DateTimeOffset.Now,
                Tags = new Dictionary<string, string> 
                { 
                    { "AlbumName", "TestAlbum" }
                }
            }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);

        // Act
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should display album price from index tag
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
