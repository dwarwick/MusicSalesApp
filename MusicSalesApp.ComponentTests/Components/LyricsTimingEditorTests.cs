#nullable enable
using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The creator's timing editor.
///
/// <para>
/// This page is now the only route a song has to listeners: alignment produces timings and stops, so
/// nothing is published until somebody has heard it here. Two things therefore matter more than the
/// rest - that it refuses a song the signed-in creator does not own, and that a half-finished edit
/// stays a draft until Publish.
/// </para>
/// </summary>
[TestFixture]
public class LyricsTimingEditorTests : BUnitTestBase
{
    private const int SongId = 1;
    private const int CreatorId = 7;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        var js = new Mock<IJSRuntime>();
        js.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .ReturnsAsync(new Mock<IJSObjectReference>().Object);
        TestContext.Services.AddSingleton(js.Object);

        SetupRendererInfo();

        var auth = AuthorizationContext;
        auth.SetAuthorized("creator@example.com");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, "3"));
        auth.SetPolicies("ManageOwnSongs");

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(new ApplicationUser { Id = 3, Email = "creator@example.com" });

        MockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new Creator { Id = CreatorId, UserId = 3 });

        MockSongMetadataService.Setup(x => x.GetByIdAsync(SongId))
            .ReturnsAsync(new SongMetadata
            {
                Id = SongId,
                SongTitle = "Five Year Plan",
                CreatorId = CreatorId,
                Mp3BlobPath = "abc/abc-music.mp3"
            });

        MockAzureStorageService.Setup(x => x.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://example.test/abc-music.mp3?sig=x"));

        MockAppSettingsService.Setup(x => x.GetLyricsConfidenceThresholdAsync()).ReturnsAsync(0.7d);
    }

    private static LyricsTimingsDocument Document() => new()
    {
        SongId = SongId,
        DurationMs = 240_000,
        Confidence = 0.52,
        Lines =
        [
            new LyricsTimedLine { Text = "[Chorus]" },
            new LyricsTimedLine
            {
                Text = "one two",
                StartMs = 10_000,
                EndMs = 12_000,
                Words =
                [
                    new LyricsTimedWord { Text = "one", StartMs = 10_000, EndMs = 11_000 },
                    new LyricsTimedWord { Text = "two", StartMs = 11_000, EndMs = 12_000 }
                ]
            }
        ]
    };

    private void GivenTimings(
        LyricsEditOutcome outcome = LyricsEditOutcome.Success,
        LyricsTimingsDocument? document = null,
        bool isDraft = false,
        double confidence = 0.52)
    {
        MockLyricsService
            .Setup(x => x.GetEditableTimingsAsync(SongId, CreatorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LyricsEditableTimings(
                outcome,
                outcome == LyricsEditOutcome.Success ? document ?? Document() : null,
                isDraft,
                new SongLyrics { SongMetadataId = SongId, Confidence = confidence }));
    }

    private IRenderedComponent<LyricsTimingEditor> Render()
    {
        var cut = TestContext.Render<LyricsTimingEditor>(pb => pb.Add(p => p.SongId, SongId));
        cut.WaitForState(() => !cut.Markup.Contains("Loading your lyrics"), TimeSpan.FromSeconds(5));
        return cut;
    }

    // -----------------------------------------------------------------
    // Who is allowed in
    // -----------------------------------------------------------------

    [Test]
    public void ASongBelongingToAnotherCreatorIsRefused()
    {
        // The route's policy says "is a creator", which is not the claim "owns this song". The
        // service makes the real check against the song's own row; this asserts the page honours it
        // rather than rendering an editor over somebody else's work.
        GivenTimings(LyricsEditOutcome.NotAllowed);

        var cut = Render();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("belongs to a different creator"));
            Assert.That(cut.FindAll(".lyrics-scroller"), Is.Empty);
        });
    }

    [Test]
    public void ASongWithNoTimingsExplainsHowToGetSome()
    {
        GivenTimings(LyricsEditOutcome.NoTimings);

        var cut = Render();

        Assert.That(cut.Markup, Does.Contain("no lyric timings yet"));
    }

    // -----------------------------------------------------------------
    // The transport
    // -----------------------------------------------------------------

    [Test]
    public void TheCreatorHearsTheWholeSongWithNoPreviewCap()
    {
        // It is their own song. A 60-second preview limit would make this page unusable for exactly
        // the person it exists for - the last chorus of a four minute song would be unreachable.
        GivenTimings();

        var cut = Render();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".restricted"), Is.Empty);
            Assert.That(cut.FindAll(".card-progress-limit-marker"), Is.Empty);
        });
    }

    [Test]
    public void ThereIsNoPreviousOrNextBecauseThereIsOneSong()
    {
        GivenTimings();

        var cut = Render();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".card-mini-controls button"), Has.Count.EqualTo(2), "Play and stop.");
            Assert.That(cut.FindAll(".card-progress-bar-container"), Is.Not.Empty, "Seekable.");
        });
    }

    [Test]
    public void HalfSpeedIsOffered()
    {
        // The single most useful affordance for tapping accurately, and the timings stay correct at
        // normal speed because they are recorded against the audio clock.
        GivenTimings();

        Assert.That(Render().Markup, Does.Contain("0.5×"));
    }

    // -----------------------------------------------------------------
    // Editing
    // -----------------------------------------------------------------

    [Test]
    public async Task SelectingAWordOffersTheNudgeControls()
    {
        GivenTimings();
        var cut = Render();

        await cut.InvokeAsync(() => cut.FindComponent<MusicSalesApp.Components.Shared.LyricsScroller>()
            .Instance.WordClicked(1, 0));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Earlier"));
            Assert.That(cut.Markup, Does.Contain("Later"));
            Assert.That(cut.Markup, Does.Contain("Play from here"));
        });
    }

    [Test]
    public async Task NudgingAWordMovesItAndMarksThePageDirty()
    {
        GivenTimings();
        var cut = Render();

        await cut.InvokeAsync(() => cut.FindComponent<MusicSalesApp.Components.Shared.LyricsScroller>()
            .Instance.WordClicked(1, 0));

        cut.FindAll("button").First(b => b.TextContent.Contains("Earlier")).Click();

        Assert.That(cut.Markup, Does.Contain("Unsaved changes"));
    }

    [Test]
    public async Task SavingWritesADraftRatherThanPublishing()
    {
        // The whole point of the draft split. A creator part way through re-tapping a chorus must not
        // be broadcasting that state, so Save touches nothing a listener can see.
        GivenTimings();
        MockLyricsService
            .Setup(x => x.SaveDraftAsync(SongId, CreatorId, It.IsAny<LyricsTimingsDocument>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LyricsEditResult.Ok("Saved."));

        var cut = Render();

        await cut.InvokeAsync(() => cut.FindComponent<MusicSalesApp.Components.Shared.LyricsScroller>()
            .Instance.WordClicked(1, 0));
        cut.FindAll("button").First(b => b.TextContent.Contains("Earlier")).Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        MockLyricsService.Verify(
            x => x.SaveDraftAsync(SongId, CreatorId, It.IsAny<LyricsTimingsDocument>(), It.IsAny<CancellationToken>()),
            Times.Once);

        MockLyricsService.Verify(
            x => x.PublishAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Saving is not publishing.");
    }

    [Test]
    public void SaveIsDisabledUntilSomethingChanges()
    {
        GivenTimings();

        var cut = Render();

        var save = cut.FindAll("button").First(b => b.TextContent.Trim() == "Save");

        Assert.That(save.HasAttribute("disabled"), Is.True);
    }

    // -----------------------------------------------------------------
    // What the creator is told
    // -----------------------------------------------------------------

    [Test]
    public void ALowScoringSongWarnsThemToExpectWork()
    {
        GivenTimings(confidence: 0.42);

        var cut = Render();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("weren't confident"));
            Assert.That(cut.Markup, Does.Not.Contain("70"), "The threshold is an admin knob, not creator-facing.");
        });
    }

    [Test]
    public void AHighScoringSongIsEncouraging()
    {
        GivenTimings(confidence: 0.88);

        Assert.That(Render().Markup, Does.Contain("came out well"));
    }

    [Test]
    public void ADraftSaysListenersStillSeeThePublishedVersion()
    {
        // "You are looking at edits you never published" is materially different from "you are
        // looking at what the aligner produced", and a creator who confuses the two will not
        // understand why listeners cannot see their work.
        GivenTimings(isDraft: true);

        Assert.That(Render().Markup, Does.Contain("still see the last version you published"));
    }

    [Test]
    public void TheInstructionsAreOnThePageRatherThanBehindALink()
    {
        // Written for somebody who has never done this. A creator who does not understand tap-along
        // will either not use it or make their timings worse, and both read to them as the feature
        // being broken.
        GivenTimings();

        var cut = Render();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("space bar"));
            Assert.That(cut.Markup, Does.Contain("Nothing here is visible to listeners until you"));
            Assert.That(cut.Markup, Does.Contain("half speed"));
            Assert.That(cut.Markup, Does.Not.Contain("milliseconds"), "No jargon.");
            Assert.That(cut.Markup, Does.Not.Contain("monotonic"));
        });
    }
}
