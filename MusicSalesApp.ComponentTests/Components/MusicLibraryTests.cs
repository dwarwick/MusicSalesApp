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
                
                UpdatedAt = DateTime.Now
            }
        };
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have album art animation (lottie) for songs without cover art
        Assert.That(cut.Markup, Does.Contain("card-album-art-animation"));
        Assert.That(cut.Markup, Does.Contain("dotlottie-wc"));
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
    public void MusicLibrary_DisplaysSongFromMetadata()
    {
        // Arrange - set up a song in metadata
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");
        
        // Set up metadata with a song
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>
            {
                new MusicSalesApp.Models.SongMetadata { Mp3BlobPath = "TestSong.mp3",  }
            });

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should display song name (price no longer displayed since individual purchases removed)
        Assert.That(cut.Markup, Does.Contain("TestSong"));
    }

    [Test]
    public void MusicLibrary_HidesTitle_WhenShowHomePageFeatured()
    {
        // Arrange - Set up empty metadata list
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>(builder => builder.Add(m => m.ShowHomePageFeatured, true));

        // Assert - should not show h3 title when ShowHomePageFeatured is true
        Assert.That(cut.Markup, Does.Not.Contain("<h3"));
    }

    [Test]
    public void MusicLibrary_HidesFilterRadioButtons_WhenShowHomePageFeatured()
    {
        // Arrange - authorize user so radio buttons would normally be visible
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>(builder => builder.Add(m => m.ShowHomePageFeatured, true));

        // Assert - should not show filter radio buttons when ShowHomePageFeatured is true
        Assert.That(cut.Markup, Does.Not.Contain("All Music"));
        Assert.That(cut.Markup, Does.Not.Contain("Not Owned"));
    }

    [Test]
    public void MusicLibrary_OnlyShowsFeaturedSongs_WhenShowHomePageFeatured()
    {
        // Arrange - Set up metadata with featured and non-featured songs
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata 
            { 
                Id = 1,
                Mp3BlobPath = "FeaturedSong.mp3",
                
                DisplayOnHomePage = true,
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata 
            { 
                Id = 2,
                Mp3BlobPath = "RegularSong.mp3",
                
                DisplayOnHomePage = false,
                UpdatedAt = DateTime.Now
            }
        };
        
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>(builder => builder.Add(m => m.ShowHomePageFeatured, true));

        // Assert - should only show featured song
        Assert.That(cut.Markup, Does.Contain("FeaturedSong"));
        Assert.That(cut.Markup, Does.Not.Contain("RegularSong"));
    }

    [Test]
    public void MusicLibrary_ShowsGenreFilterPill()
    {
        // Arrange - Set up empty metadata list
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have genre filter pill
        Assert.That(cut.Markup, Does.Contain("genre-filter-pill"));
        Assert.That(cut.Markup, Does.Contain("Genres"));
    }

    [Test]
    public void MusicLibrary_GenreFilterDropdown_TogglesOnClick()
    {
        // Arrange
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>
            {
                new MusicSalesApp.Models.SongMetadata
                {
                    Mp3BlobPath = "Song1.mp3",
                    Genre = "Rock",
                    UpdatedAt = DateTime.Now
                }
            });

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - dropdown should not be visible initially
        Assert.That(cut.Markup, Does.Not.Contain("genre-dropdown"));

        // Act - click the pill to open dropdown
        cut.Find(".genre-filter-pill").Click();

        // Assert - dropdown should now be visible with genres and count
        Assert.That(cut.Markup, Does.Contain("genre-dropdown"));
        Assert.That(cut.Markup, Does.Contain("Rock (1)"));
    }

    [Test]
    public void MusicLibrary_GenreFilter_FiltersSongsByGenre()
    {
        // Arrange - Set up metadata with multiple genres
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "RockSong.mp3",
                Genre = "Rock",
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "JazzSong.mp3",
                Genre = "Jazz",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - both songs should be visible initially
        Assert.That(cut.Markup, Does.Contain("RockSong"));
        Assert.That(cut.Markup, Does.Contain("JazzSong"));

        // Act - open dropdown and check Rock
        cut.Find(".genre-filter-pill").Click();
        var checkboxes = cut.FindAll(".genre-dropdown-item input[type='checkbox']");
        // Find the Rock checkbox
        var rockCheckbox = checkboxes.FirstOrDefault(cb =>
            cb.ParentElement.TextContent.Contains("Rock"));
        rockCheckbox?.Change(true);

        // Assert - only Rock song should be visible
        Assert.That(cut.Markup, Does.Contain("RockSong"));
        Assert.That(cut.Markup, Does.Not.Contain("JazzSong"));
    }

    [Test]
    public void MusicLibrary_GenreFilter_ShowsCountWhenActive()
    {
        // Arrange
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "RockSong.mp3",
                Genre = "Rock",
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "JazzSong.mp3",
                Genre = "Jazz",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Act - open dropdown and select a genre
        cut.Find(".genre-filter-pill").Click();
        var checkboxes = cut.FindAll(".genre-dropdown-item input[type='checkbox']");
        var rockCheckbox = checkboxes.FirstOrDefault(cb =>
            cb.ParentElement.TextContent.Contains("Rock"));
        rockCheckbox?.Change(true);

        // Assert - should show count badge
        Assert.That(cut.Markup, Does.Contain("genre-filter-count"));
        Assert.That(cut.Find(".genre-filter-count").TextContent, Is.EqualTo("1"));
    }

    [Test]
    public void MusicLibrary_GenreFilter_ClearResetsFilter()
    {
        // Arrange
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "RockSong.mp3",
                Genre = "Rock",
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "JazzSong.mp3",
                Genre = "Jazz",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Act - select a genre to filter
        cut.Find(".genre-filter-pill").Click();
        var checkboxes = cut.FindAll(".genre-dropdown-item input[type='checkbox']");
        var rockCheckbox = checkboxes.FirstOrDefault(cb =>
            cb.ParentElement.TextContent.Contains("Rock"));
        rockCheckbox?.Change(true);

        // Verify filter is active
        Assert.That(cut.Markup, Does.Not.Contain("JazzSong"));

        // Act - click clear button
        cut.Find(".genre-clear-x").Click();

        // Assert - all songs should be visible again
        Assert.That(cut.Markup, Does.Contain("RockSong"));
        Assert.That(cut.Markup, Does.Contain("JazzSong"));
    }

    [Test]
    public void MusicLibrary_GenreFilter_HiddenWhenShowHomePageFeatured()
    {
        // Arrange
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>(builder => builder.Add(m => m.ShowHomePageFeatured, true));

        // Assert - genre filter should not be shown
        Assert.That(cut.Markup, Does.Not.Contain("genre-filter-pill"));
    }

    [Test]
    public void MusicLibrary_GenreFilter_SearchFiltersDropdownList()
    {
        // Arrange
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "RockSong.mp3",
                Genre = "Rock",
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "JazzSong.mp3",
                Genre = "Jazz",
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "CountrySong.mp3",
                Genre = "Country",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Act - open dropdown
        cut.Find(".genre-filter-pill").Click();

        // Assert - search input should be present and all genres visible
        Assert.That(cut.Markup, Does.Contain("genre-search-input"));
        Assert.That(cut.Markup, Does.Contain("Rock"));
        Assert.That(cut.Markup, Does.Contain("Jazz"));
        Assert.That(cut.Markup, Does.Contain("Country"));

        // Act - type in search to filter
        cut.Find(".genre-search-input").Input("ro");

        // Assert - only Rock should be visible in the dropdown list
        var dropdownItems = cut.FindAll(".genre-dropdown-item");
        Assert.That(dropdownItems, Has.Count.EqualTo(1));
        Assert.That(dropdownItems[0].TextContent, Does.Contain("Rock"));
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
