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
    public void MusicLibrary_HasPlayButtonAndTappableArt_ForEachCard()
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

        // Assert - the actions row holds Play, and the artwork itself is the second way in.
        // It used to hold Play plus an eyeball "view" button; that button is gone.
        Assert.That(cut.Markup, Does.Contain("card-actions"));
        Assert.That(cut.Markup, Does.Contain("title=\"play\""));
        Assert.That(cut.Markup, Does.Contain("card-art-open"));
        Assert.That(cut.Markup, Does.Not.Contain("title=\"view\""));
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
    public void MusicLibrary_OpensThePlayerFromTheArtwork_NotAViewButton()
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

        // The eyeball "view" button is gone. .card-art-open - a transparent button covering the
        // whole artwork - is what calls OpenSongPlayer and navigates to /song/{title} now.
        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll("button[title='view']"), Is.Empty,
                "the eyeball view button was replaced by tap-to-open on the artwork");

            var artOpen = cut.FindAll(".card-album-art .card-art-open");
            Assert.That(artOpen, Is.Not.Empty, "every card needs a way into the player");
            Assert.That(artOpen[0].GetAttribute("aria-label"), Is.Not.Null.And.Not.Empty,
                "a transparent button has no text, so the accessible name has to come from aria-label");
        });
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
    public void MusicLibrary_RendersLikeDislikeButtons_InAiActionsRow()
    {
        // Arrange - an AI song so both the badges and the buttons share the row
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 1,
                Mp3BlobPath = "AiSong.mp3",
                SongTitle = "AI Song",
                IsAiGenerated = true,
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - the like/dislike buttons live inside the AI badges row, not the bottom action row
        var aiActionsRow = cut.Find(".card-ai-actions-row");
        Assert.Multiple(() =>
        {
            Assert.That(aiActionsRow.QuerySelector(".card-ai-content-badges"), Is.Not.Null);
            Assert.That(aiActionsRow.QuerySelector(".like-button"), Is.Not.Null);
            Assert.That(aiActionsRow.QuerySelector(".dislike-button"), Is.Not.Null);
            Assert.That(cut.Find(".card-actions-with-likes").QuerySelector(".like-dislike-container"), Is.Null);
        });
    }

    [Test]
    public void MusicLibrary_RendersAiActionsRowWithLikeButtons_ForSongWithoutAiFlags()
    {
        // Arrange - no AI flags, so AiContentBadges renders nothing at all
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 2,
                Mp3BlobPath = "PlainSong.mp3",
                SongTitle = "Plain Song",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Assert - the row and its buttons still render (they are right-aligned via margin-left: auto)
        var aiActionsRow = cut.Find(".card-ai-actions-row");
        Assert.Multiple(() =>
        {
            Assert.That(aiActionsRow.QuerySelector(".ai-content-badges"), Is.Null);
            Assert.That(aiActionsRow.QuerySelector(".like-button"), Is.Not.Null);
            Assert.That(aiActionsRow.QuerySelector(".dislike-button"), Is.Not.Null);
        });
    }

    /// <summary>
    /// The lyrics toggle belongs to the card being played, and only when they are published.
    ///
    /// <para>
    /// Both halves matter. The scroller follows the audio element's own clock, and only the playing
    /// card has one, so offering the toggle anywhere else would open a panel that could never
    /// highlight anything. And the gate is the row's status, not whether timings exist: since
    /// alignment stopped publishing, every successful run lands in <c>NeedsReview</c> with its
    /// timings at exactly the blob path a published song uses, so "there is a file" would show
    /// listeners work no creator had approved.
    /// </para>
    /// </summary>
    [TestCase(MusicSalesApp.Models.SongLyricsStatus.Published, true)]
    [TestCase(MusicSalesApp.Models.SongLyricsStatus.NeedsReview, false)]
    [TestCase(MusicSalesApp.Models.SongLyricsStatus.Pending, false)]
    [TestCase(MusicSalesApp.Models.SongLyricsStatus.Failed, false)]
    public void MusicLibrary_OffersTheLyricsToggle_OnlyForPublishedLyrics(
        MusicSalesApp.Models.SongLyricsStatus status,
        bool expected)
    {
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 3,
                Mp3BlobPath = "TestSong.mp3",
                SongTitle = "Test Song",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync()).ReturnsAsync(metadata);

        MockLyricsService
            .Setup(x => x.GetForSongsAsync(
                It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, MusicSalesApp.Models.SongLyrics>
            {
                [3] = new MusicSalesApp.Models.SongLyrics
                {
                    SongMetadataId = 3,
                    Status = status,
                    TimingsBlobPath = "abc/abc-lyrics.json"
                }
            });

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Nothing is offered until the card is playing, whatever its status.
        Assert.That(cut.FindAll(".lyrics-toggle-button"), Is.Empty, "Not before playback.");

        cut.Find("button[title='play']").Click();

        Assert.That(
            cut.FindAll(".lyrics-toggle-button").Count > 0,
            Is.EqualTo(expected),
            $"Status {status} should {(expected ? "offer" : "not offer")} the toggle.");
    }

    [Test]
    public void MusicLibrary_SurvivesALyricsLookupThatReturnsNothing()
    {
        // The lookup is best-effort and feeds a TryGetValue on every card drawn. A null answer must
        // cost the toggle, not the library - this took the whole page down with a
        // NullReferenceException before it was coalesced.
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 3,
                Mp3BlobPath = "TestSong.mp3",
                SongTitle = "Test Song",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync()).ReturnsAsync(metadata);

        MockLyricsService
            .Setup(x => x.GetForSongsAsync(
                It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, MusicSalesApp.Models.SongLyrics>)null);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        Assert.DoesNotThrow(() => cut.Find("button[title='play']").Click());
        Assert.That(cut.Markup, Does.Contain("card-mini-player"));
    }

    [Test]
    public void MusicLibrary_KeepsLikeDislikeButtons_WhenCardIsPlaying()
    {
        // Arrange
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new MusicSalesApp.Models.SongMetadata
            {
                Id = 3,
                Mp3BlobPath = "TestSong.mp3",
                SongTitle = "Test Song",
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Act - start playback, which swaps the bottom action row for the mini player
        cut.Find("button[title='play']").Click();

        // Assert - the mini player is showing but the like/dislike buttons are still available
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("card-mini-player"));
            Assert.That(cut.Markup, Does.Not.Contain("card-actions-with-likes"));
            Assert.That(cut.Find(".card-ai-actions-row").QuerySelector(".like-button"), Is.Not.Null);
            Assert.That(cut.Find(".card-ai-actions-row").QuerySelector(".dislike-button"), Is.Not.Null);
        });
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

    [Test]
    public void MusicLibrary_KeepsTheMiniPlayerOnThePlayingCard_WhenAFilterRemovesAnEarlierCard()
    {
        // The card loop is keyed on the file name. Without @key, Blazor diffs the list
        // POSITIONALLY, so filtering out a card that sits before the playing one shifts every
        // later card's DOM up a slot - and _activeAudioElement, _activeProgressBarElement and
        // _activeVolumeBarElement are single shared fields, so they would silently re-bind to
        // a different song's elements while that song kept playing.
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "AlphaSong.mp3", SongTitle = "Alpha Song", Genre = "Rock", UpdatedAt = DateTime.Now },
            new() { Id = 2, Mp3BlobPath = "BravoSong.mp3", SongTitle = "Bravo Song", Genre = "Jazz", UpdatedAt = DateTime.Now },
            new() { Id = 3, Mp3BlobPath = "CharlieSong.mp3", SongTitle = "Charlie Song", Genre = "Rock", UpdatedAt = DateTime.Now }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync()).ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        // Play the SECOND card, so there is a card before it for the filter to remove.
        var playButtons = cut.FindAll("button[title='play']");
        Assert.That(playButtons, Has.Count.EqualTo(3), "expected one play button per card");
        playButtons[1].Click();

        Assert.That(CardContainingMiniPlayer(cut).TextContent, Does.Contain("Bravo Song"),
            "the mini player should start on the card that was clicked");

        // Narrow to Jazz, which drops AlphaSong (before the playing card) and CharlieSong (after).
        FindFilterPill(cut, "Genres").Click();
        cut.FindAll(".filter-pill-dropdown-item input[type='checkbox']")
            .First(cb => cb.ParentElement!.TextContent.Contains("Jazz", StringComparison.Ordinal))
            .Change(true);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("AlphaSong"));
            Assert.That(cut.Markup, Does.Not.Contain("CharlieSong"));
            // The surviving card is still the one that is playing, and it still owns the audio
            // element the JS module was handed a reference to.
            Assert.That(CardContainingMiniPlayer(cut).TextContent, Does.Contain("Bravo Song"));
            Assert.That(cut.FindAll(".card-mini-player"), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void MusicLibrary_UsesTheCarouselSizesLadder_OnlyWhenRenderedAsTheHomeCarousel()
    {
        // The two containers size their art completely differently below 576px: the grid card
        // turns horizontal with 84px art, while the carousel card stays square at ~83vw. One
        // sizes string cannot describe both, so the component picks per mode.
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new()
            {
                Id = 1,
                Mp3BlobPath = "TestSong.mp3",
                SongTitle = "Test Song",
                ImageBlobPath = "art/test.jpg",
                DisplayOnHomePage = true,
                UpdatedAt = DateTime.Now
            }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync()).ReturnsAsync(metadata);

        // The base fixture returns CoverArtSource.None, which sends the card down the Lottie
        // fallback branch - and CoverArt is then never rendered at all.
        MockCoverArtUrlBuilder
            .Setup(b => b.BuildProxy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new MusicSalesApp.Services.CoverArtSource(
                "/api/music/art/test.jpg",
                Array.Empty<MusicSalesApp.Services.CoverArtVariantUrl>()));

        SetupRendererInfo();

        var grid = TestContext.Render<MusicLibrary>();
        var carousel = TestContext.Render<MusicLibrary>(p => p.Add(c => c.ShowHomePageFeatured, true));

        Assert.Multiple(() =>
        {
            Assert.That(grid.Markup, Does.Contain("music-cards-grid"));
            Assert.That(carousel.Markup, Does.Contain("featured-music-carousel"));
            Assert.That(
                grid.FindComponent<MusicSalesApp.Components.Shared.CoverArt>().Instance.Sizes,
                Is.EqualTo(MusicSalesApp.Components.Shared.CoverArtSizes.Card));
            Assert.That(
                carousel.FindComponent<MusicSalesApp.Components.Shared.CoverArt>().Instance.Sizes,
                Is.EqualTo(MusicSalesApp.Components.Shared.CoverArtSizes.CardCarousel));
        });
    }

    [Test]
    public void MusicLibrary_ShipsNoInlineStyleBlock()
    {
        // It used to carry an inline <style> setting .e-card.music-card padding to 8px. Being
        // injected only when this component rendered, it made the SAME playlist card 8px-padded
        // on / (where this is embedded) and 16px-padded on /my-playlists (where it is not).
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        var cut = TestContext.Render<MusicLibrary>();

        Assert.That(cut.Markup, Does.Not.Contain("<style"),
            "card styling belongs in the global sheets, not in per-component markup");
    }

    [Test]
    public void MusicLibrary_MarksPlayAsTheOnlyAction()
    {
        // The CSS keys the accent fill off .action-button-play. Both buttons used to carry the
        // identical .action-button outline, which left the card with no primary action at all -
        // and made the full-width Facebook share bar the loudest control on it. The View button
        // has since gone entirely, so Play is the row's only child and stretches full width.
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "TestSong.mp3", SongTitle = "Test Song", UpdatedAt = DateTime.Now }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync()).ReturnsAsync(metadata);
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Find("button[title='play']").ClassList, Does.Contain("action-button-play"));
            Assert.That(cut.Find(".card-actions-with-likes").QuerySelectorAll(".action-column"),
                Has.Length.EqualTo(1), "Play is the only action column left");
            Assert.That(cut.Markup, Does.Not.Contain("action-button-view"));
        });
    }

    [Test]
    public void MusicLibrary_GroupsArtistGenreAndStreamsInOneMetaWrapper()
    {
        // .card-meta is display:contents at 576px and up, so it is invisible to every wider
        // layout - but xs_app.css flips it to flex to merge the three onto one line. If the
        // wrapper stops containing all three, the phone card silently falls back to a stack.
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "TestSong.mp3", SongTitle = "Test Song", UpdatedAt = DateTime.Now }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync()).ReturnsAsync(metadata);
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        var meta = cut.Find(".card-meta");
        Assert.Multiple(() =>
        {
            Assert.That(meta.QuerySelector(".card-genre"), Is.Not.Null);
            Assert.That(meta.QuerySelector(".card-stream-count"), Is.Not.Null);
        });
    }

    [Test]
    public void MusicLibrary_ExplainsItself_WhenFiltersExcludeEverything()
    {
        // Before this the page rendered an EMPTY GRID: no message, and no hint that a filter was
        // responsible. The only recovery cue was spotting the small clear glyph on each pill.
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "HumanSong.mp3", SongTitle = "Human Song", Genre = "Folk", UpdatedAt = DateTime.Now }
        };

        MockSongMetadataService.Setup(x => x.GetAllAsync()).ReturnsAsync(metadata);
        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        Assert.That(cut.FindAll(".empty-state"), Is.Empty, "nothing is filtered yet");

        // Narrow to AI music, of which there is none.
        FindFilterPill(cut, "Music Type").Click();
        cut.FindAll(".filter-pill-dropdown-option")
            .First(o => o.TextContent.Contains("AI Music", StringComparison.Ordinal))
            .Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("Human Song"));
            Assert.That(cut.Find(".empty-state-title").TextContent, Is.EqualTo("No songs match these filters"));
        });

        // ...and the state can undo itself.
        cut.Find(".empty-state .state-action").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".empty-state"), Is.Empty);
            Assert.That(cut.Markup, Does.Contain("Human Song"));
        });
    }

    [Test]
    public void MusicLibrary_RecoversFromAFailedLoad_WhenRetryIsClicked()
    {
        // _error was set but never cleared - LoadFiles's finally resets only _loading - so one
        // transient failure blanked the page for the rest of the circuit with no way back.
        var metadata = new List<MusicSalesApp.Models.SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "TestSong.mp3", SongTitle = "Test Song", UpdatedAt = DateTime.Now }
        };

        MockSongMetadataService.SetupSequence(x => x.GetAllAsync())
            .ThrowsAsync(new InvalidOperationException("catalogue unavailable"))
            .ReturnsAsync(metadata);

        SetupRendererInfo();
        var cut = TestContext.Render<MusicLibrary>();

        Assert.That(cut.Find(".error-state").TextContent, Does.Contain("catalogue unavailable"));

        cut.Find(".error-state .state-action").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".error-state"), Is.Empty, "the error must clear, not just re-run underneath it");
            Assert.That(cut.Markup, Does.Contain("Test Song"));
        });
    }

    private static IElement CardContainingMiniPlayer(IRenderedComponent<MusicLibrary> cut)
    {
        return cut.FindAll(".music-card").Single(card => card.QuerySelector(".card-mini-player") != null);
    }

    private static IElement FindFilterPill(IRenderedComponent<MusicLibrary> cut, string label)
    {
        return cut.FindAll(".filter-pill")
            .First(pill => pill.TextContent.Contains(label, StringComparison.Ordinal));
    }
}
