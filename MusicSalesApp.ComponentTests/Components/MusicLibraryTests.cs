using Bunit;
using AngleSharp.Dom;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Components.Pages.Public;
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
        SetupRendererInfo();
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
        SetupAuthorizedUser(1, "testuser");
        
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
        SetupAuthorizedUser(1, "testuser");
        
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
    public void MusicLibrary_OrdersFeaturedSongs_ByNullDisplayOrderThenDisplayOrder()
    {
        // Arrange
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 10,
                Mp3BlobPath = "RankedOne.mp3",
                SongTitle = "Ranked One",
                DisplayOnHomePage = true,
                DisplayOrder = 1,
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 40,
                Mp3BlobPath = "RankedTwo.mp3",
                SongTitle = "Ranked Two",
                DisplayOnHomePage = true,
                DisplayOrder = 2,
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 30,
                Mp3BlobPath = "NullNewest.mp3",
                SongTitle = "Null Newest",
                DisplayOnHomePage = true,
                DisplayOrder = null,
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 20,
                Mp3BlobPath = "NullOlder.mp3",
                SongTitle = "Null Older",
                DisplayOnHomePage = true,
                DisplayOrder = null,
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>(builder => builder.Add(m => m.ShowHomePageFeatured, true));

        // Assert
        var titles = cut.FindAll(".card-song-title")
            .Select(element => element.TextContent.Trim())
            .ToList();

        Assert.That(titles, Is.EqualTo(new[]
        {
            "Null Newest",
            "Null Older",
            "Ranked One",
            "Ranked Two"
        }));
    }

    [Test]
    public void MusicLibrary_ShowsGenreFilterPill()
    {
        // Arrange - Set up empty metadata list
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have filter pill with Genres label
        Assert.That(cut.Markup, Does.Contain("filter-pill"));
        Assert.That(cut.Markup, Does.Contain("Genres"));
    }

    [Test]
    public void MusicLibrary_ShowsArtistFilterPill()
    {
        // Arrange - Set up empty metadata list
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - should have filter pill with Artists label
        Assert.That(cut.Markup, Does.Contain("Artists"));
        var pills = cut.FindAll(".filter-pill");
        Assert.That(pills.Count, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void MusicLibrary_ShowsAiFilterPill()
    {
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        var cut = TestContext.Render<MusicLibrary>();

        Assert.That(cut.Markup, Does.Contain("Music Type"));
        Assert.That(cut.Markup, Does.Contain("filter-pill"));
    }

    [Test]
    public void MusicLibrary_RendersAiBadge_ForAiGeneratedSong()
    {
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "AiSong.mp3",
                SongTitle = "AI Song",
                IsAiGenerated = true,
                IsAiVocals = true,
                IsAiLyrics = true,
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        Assert.That(cut.Markup, Does.Contain("music_icon_24.png"));
        Assert.That(cut.Markup, Does.Contain("vocals_icon_24.png"));
        Assert.That(cut.Markup, Does.Contain("lyrics_icon_24.png"));
    }

    [Test]
    public void MusicLibrary_AiFilter_FiltersByAnyAndIndividualAiFlags()
    {
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "AiMusic.mp3", SongTitle = "AI Music", IsAiGenerated = true, UpdatedAt = DateTime.Now },
            new() { Id = 2, Mp3BlobPath = "AiVocals.mp3", SongTitle = "AI Vocals", IsAiVocals = true, UpdatedAt = DateTime.Now },
            new() { Id = 3, Mp3BlobPath = "AiLyrics.mp3", SongTitle = "AI Lyrics", IsAiLyrics = true, UpdatedAt = DateTime.Now },
            new() { Id = 4, Mp3BlobPath = "Human.mp3", SongTitle = "Human Song", UpdatedAt = DateTime.Now }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        FindFilterPill(cut, "Music Type").Click();

        Assert.That(cut.Markup, Does.Contain("filter-pill-option-icon"));
        Assert.That(cut.Markup, Does.Contain("music_icon_24.png"));
        Assert.That(cut.Markup, Does.Contain("vocals_icon_24.png"));
        Assert.That(cut.Markup, Does.Contain("lyrics_icon_24.png"));

        cut.FindAll(".filter-pill-dropdown-option")
            .First(option => option.TextContent.Contains("Any AI", StringComparison.Ordinal))
            .Click();

        Assert.That(cut.Markup, Does.Contain("AI Music"));
        Assert.That(cut.Markup, Does.Contain("AI Vocals"));
        Assert.That(cut.Markup, Does.Contain("AI Lyrics"));
        Assert.That(cut.Markup, Does.Not.Contain("Human Song"));

        FindFilterPill(cut, "Any AI").Click();
        cut.FindAll(".filter-pill-dropdown-option")
            .First(option => option.TextContent.Contains("AI Vocals", StringComparison.Ordinal))
            .Click();

        Assert.That(cut.Markup, Does.Not.Contain("AI Music"));
        Assert.That(cut.Markup, Does.Contain("AI Vocals"));
        Assert.That(cut.Markup, Does.Not.Contain("AI Lyrics"));
        Assert.That(cut.Markup, Does.Not.Contain("Human Song"));
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
        Assert.That(cut.Markup, Does.Not.Contain("filter-pill-dropdown"));

        // Act - click the genre pill to open dropdown
        FindFilterPill(cut, "Genres").Click();

        // Assert - dropdown should now be visible with genres and count
        Assert.That(cut.Markup, Does.Contain("filter-pill-dropdown"));
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

        // Act - open genre dropdown and check Rock
        FindFilterPill(cut, "Genres").Click();
        var checkboxes = cut.FindAll(".filter-pill-dropdown-item input[type='checkbox']");
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

        // Act - open genre dropdown and select a genre
        FindFilterPill(cut, "Genres").Click();
        var checkboxes = cut.FindAll(".filter-pill-dropdown-item input[type='checkbox']");
        var rockCheckbox = checkboxes.FirstOrDefault(cb =>
            cb.ParentElement.TextContent.Contains("Rock"));
        rockCheckbox?.Change(true);

        // Assert - should show count badge
        Assert.That(cut.Markup, Does.Contain("filter-pill-count"));
        Assert.That(cut.Find(".filter-pill-count").TextContent, Is.EqualTo("1"));
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
        FindFilterPill(cut, "Genres").Click();
        var checkboxes = cut.FindAll(".filter-pill-dropdown-item input[type='checkbox']");
        var rockCheckbox = checkboxes.FirstOrDefault(cb =>
            cb.ParentElement.TextContent.Contains("Rock"));
        rockCheckbox?.Change(true);

        // Verify filter is active
        Assert.That(cut.Markup, Does.Not.Contain("JazzSong"));

        // Act - click clear button
        cut.Find(".filter-pill-clear").Click();

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

        // Assert - filter pills should not be shown
        Assert.That(cut.Markup, Does.Not.Contain("filter-pill"));
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

        // Act - open genre dropdown
        FindFilterPill(cut, "Genres").Click();

        // Assert - search input should be present and all genres visible
        Assert.That(cut.Markup, Does.Contain("filter-pill-search-input"));
        Assert.That(cut.Markup, Does.Contain("Rock"));
        Assert.That(cut.Markup, Does.Contain("Jazz"));
        Assert.That(cut.Markup, Does.Contain("Country"));

        // Act - type in search to filter
        cut.Find(".filter-pill-search-input").Input("ro");

        // Assert - only Rock should be visible in the dropdown list
        var dropdownItems = cut.FindAll(".filter-pill-dropdown-item");
        Assert.That(dropdownItems, Has.Count.EqualTo(1));
        Assert.That(dropdownItems[0].TextContent, Does.Contain("Rock"));
    }

    [Test]
    public void MusicLibrary_ArtistFilter_FiltersSongsByArtist()
    {
        // Arrange - Set up metadata with multiple artists
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "Song1.mp3",
                Genre = "Rock",
                ArtistName = "Artist A",
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "Song2.mp3",
                Genre = "Jazz",
                ArtistName = "Artist B",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - both songs should be visible initially
        Assert.That(cut.Markup, Does.Contain("Song1"));
        Assert.That(cut.Markup, Does.Contain("Song2"));

        // Act - open artist dropdown and select Artist A
        FindFilterPill(cut, "Artists").Click();
        var checkboxes = cut.FindAll(".filter-pill-dropdown-item input[type='checkbox']");
        var artistACheckbox = checkboxes.FirstOrDefault(cb =>
            cb.ParentElement.TextContent.Contains("Artist A"));
        artistACheckbox?.Change(true);

        // Assert - only Song1 by Artist A should be visible
        Assert.That(cut.Markup, Does.Contain("Song1"));
        Assert.That(cut.Markup, Does.Not.Contain("Song2"));
    }

    [Test]
    public void MusicLibrary_ArtistFilter_HiddenWhenShowHomePageFeatured()
    {
        // Arrange
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>(builder => builder.Add(m => m.ShowHomePageFeatured, true));

        // Assert - artist filter should not be shown
        Assert.That(cut.Markup, Does.Not.Contain("Artists"));
    }

    [Test]
    public void MusicLibrary_CrossFilter_ArtistFilterUpdatesGenreItems()
    {
        // Arrange - Set up metadata with multiple artists and genres
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "Song1.mp3",
                Genre = "Rock",
                ArtistName = "Artist A",
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "Song2.mp3",
                Genre = "Jazz",
                ArtistName = "Artist B",
                UpdatedAt = DateTime.Now
            },
            new MusicSalesApp.Models.SongMetadata
            {
                Mp3BlobPath = "Song3.mp3",
                Genre = "Country",
                ArtistName = "Artist A",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Act - open artist dropdown and select Artist A
        FindFilterPill(cut, "Artists").Click();
        var checkboxes = cut.FindAll(".filter-pill-dropdown-item input[type='checkbox']");
        var artistACheckbox = checkboxes.FirstOrDefault(cb =>
            cb.ParentElement.TextContent.Contains("Artist A"));
        artistACheckbox?.Change(true);

        // Now open genre dropdown to see updated items
        FindFilterPill(cut, "Genres").Click();

        // Assert - genre dropdown should only show Rock and Country (Artist A's genres), not Jazz
        var genreItems = cut.FindAll(".filter-pill-dropdown-item");
        var genreTexts = genreItems.Select(i => i.TextContent).ToList();
        Assert.That(genreTexts.Any(t => t.Contains("Rock")), Is.True);
        Assert.That(genreTexts.Any(t => t.Contains("Country")), Is.True);
        Assert.That(genreTexts.Any(t => t.Contains("Jazz")), Is.False);
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

    private static IElement FindFilterPill(IRenderedComponent<MusicLibrary> cut, string label)
    {
        return cut.FindAll(".filter-pill")
            .First(pill => pill.TextContent.Contains(label, StringComparison.Ordinal));
    }
}
