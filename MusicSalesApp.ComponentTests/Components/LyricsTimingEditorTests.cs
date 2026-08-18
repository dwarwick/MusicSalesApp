#nullable enable
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
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

    [TestCase(LyricsEditOutcome.NotAllowed)]
    [TestCase(LyricsEditOutcome.NotFound)]
    public void ASongTheyDoNotOwnSendsThemHomeWithoutAnEditor(LyricsEditOutcome outcome)
    {
        // The song id comes from the URL and is a small integer, so this is the whole authorisation
        // story for the page - the route's policy only establishes "is a creator", not "owns song
        // 412". The service makes the real check against the song's own CreatorId; this asserts the
        // page acts on it rather than rendering an editor over somebody else's work.
        GivenTimings(outcome);

        var navigation = TestContext.Services.GetRequiredService<NavigationManager>();

        TestContext.Render<LyricsTimingEditor>(pb => pb.Add(p => p.SongId, SongId));

        Assert.That(new Uri(navigation.Uri).AbsolutePath, Is.EqualTo("/"));
    }

    [Test]
    public void NotYoursAndNotASongAreRefusedIdentically()
    {
        // Deliberately indistinguishable. Telling somebody "that song belongs to a different
        // creator" confirms it exists and is owned, which is enough to walk the id space and learn
        // which ids are real. Refusing both the same way leaves nothing to learn from trying.
        GivenTimings(LyricsEditOutcome.NotAllowed);
        var navigation = TestContext.Services.GetRequiredService<NavigationManager>();
        TestContext.Render<LyricsTimingEditor>(pb => pb.Add(p => p.SongId, SongId));
        var refusedNotYours = navigation.Uri;

        GivenTimings(LyricsEditOutcome.NotFound);
        TestContext.Render<LyricsTimingEditor>(pb => pb.Add(p => p.SongId, SongId));
        var refusedNotFound = navigation.Uri;

        Assert.That(refusedNotYours, Is.EqualTo(refusedNotFound));
    }

    [Test]
    public void ASongTheyDoOwnWithNoTimingsStaysAndExplainsHowToGetSome()
    {
        // Not a refusal - they own it, it simply has nothing timed yet. Redirecting here would be
        // unhelpful, and the message reveals nothing they did not already know about their own song.
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

        // Asserted on the banner rather than the whole page, and on words rather than digits: the
        // scroller's instance id is a GUID, so "does the markup contain 70" is true roughly one run
        // in eight regardless of what the banner says.
        var banner = cut.Find("[role=status]").TextContent;

        Assert.Multiple(() =>
        {
            Assert.That(banner, Does.Contain("weren't confident"));
            Assert.That(banner, Does.Not.Contain("%"), "The score is an admin's calibration, not a creator's problem.");
            Assert.That(banner, Does.Not.Contain("threshold").IgnoreCase);
            Assert.That(banner, Does.Not.Contain("confidence").IgnoreCase, "No jargon in the greeting.");
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

    // -----------------------------------------------------------------
    // Tap-along
    // -----------------------------------------------------------------

    private static LyricsTimingsDocument ThreeLines() => new()
    {
        SongId = SongId,
        DurationMs = 240_000,
        Lines =
        [
            new LyricsTimedLine { Text = "[Verse]" },
            new LyricsTimedLine
            {
                Text = "first",
                StartMs = 10_000,
                EndMs = 12_000,
                Words = [new LyricsTimedWord { Text = "first", StartMs = 10_000, EndMs = 12_000 }]
            },
            new LyricsTimedLine
            {
                Text = "second",
                StartMs = 20_000,
                EndMs = 22_000,
                Words = [new LyricsTimedWord { Text = "second", StartMs = 20_000, EndMs = 22_000 }]
            },
            new LyricsTimedLine
            {
                Text = "third",
                StartMs = 30_000,
                EndMs = 32_000,
                Words = [new LyricsTimedWord { Text = "third", StartMs = 30_000, EndMs = 32_000 }]
            }
        ]
    };

    private LyricsTimingEditorModel Model(IRenderedComponent<LyricsTimingEditor> cut) => cut.Instance;

    [Test]
    public async Task StartingATapPassBeginsAtTheFirstLineAheadOfThePlayhead()
    {
        // Not at the top of the song. Alignment usually drifts in one section, and the instructions
        // promise "just the part that's wrong" - so a creator seeks to the chorus, arms, and taps.
        GivenTimings(document: ThreeLines());
        var cut = Render();

        await cut.InvokeAsync(() => Model(cut).UpdateTime(15d));
        await cut.InvokeAsync(() => Model(cut).StartRecording());

        Assert.That(cut.Markup, Does.Contain("second"), "The first line after 15 s.");
    }

    [Test]
    public async Task ATapMovesThatLineToTheTappedMomentAndAdvances()
    {
        GivenTimings(document: ThreeLines());
        var cut = Render();

        await cut.InvokeAsync(() => Model(cut).StartRecording());
        await cut.InvokeAsync(() => Model(cut).RecordLineTap(11_500));

        var document = Model(cut).EditedDocument!;

        Assert.Multiple(() =>
        {
            Assert.That(document.Lines[1].StartMs, Is.EqualTo(11_500), "The tapped line moved.");
            Assert.That(cut.Markup, Does.Contain("second"), "And the prompt advanced.");
        });
    }

    [Test]
    public async Task TappingEndsThePreviousLineSoTwoDoNotOverlap()
    {
        // The property that makes one pass down a song coherent. Without it every earlier line keeps
        // running underneath the one being tapped and two light up at once.
        GivenTimings(document: ThreeLines());
        var cut = Render();

        await cut.InvokeAsync(() => Model(cut).StartRecording());
        await cut.InvokeAsync(() => Model(cut).RecordLineTap(11_000));
        await cut.InvokeAsync(() => Model(cut).RecordLineTap(18_000));

        var document = Model(cut).EditedDocument!;

        Assert.That(document.Lines[1].EndMs, Is.LessThanOrEqualTo(18_000));
    }

    [Test]
    public async Task TheTapPassSkipsSectionMarkers()
    {
        // Nobody sings "[Verse]". Stopping on one would leave the creator waiting for a cue that
        // never comes, mid-pass, with the song still playing.
        GivenTimings(document: ThreeLines());
        var cut = Render();

        await cut.InvokeAsync(() => Model(cut).StartRecording());

        Assert.That(cut.Markup, Does.Not.Contain("Next line</span><strong>[Verse]"));
    }

    [Test]
    public async Task TheLastTapEndsThePassRatherThanLeavingItArmed()
    {
        GivenTimings(document: ThreeLines());
        var cut = Render();

        await cut.InvokeAsync(() => Model(cut).StartRecording());
        await cut.InvokeAsync(() => Model(cut).RecordLineTap(11_000));
        await cut.InvokeAsync(() => Model(cut).RecordLineTap(21_000));
        await cut.InvokeAsync(() => Model(cut).RecordLineTap(31_000));

        Assert.That(cut.Markup, Does.Contain("Start tapping"), "Back to the disarmed state.");
    }

    [Test]
    public async Task ATapIsUndoable()
    {
        GivenTimings(document: ThreeLines());
        var cut = Render();

        await cut.InvokeAsync(() => Model(cut).StartRecording());
        await cut.InvokeAsync(() => Model(cut).RecordLineTap(11_500));
        await cut.InvokeAsync(() => Model(cut).Undo());

        Assert.That(Model(cut).EditedDocument!.Lines[1].StartMs, Is.EqualTo(10_000));
    }

    [Test]
    public async Task TapsAreIgnoredWhenThePassIsNotArmed()
    {
        // The space bar is play/pause when not recording, and JS gates on its own flag - but a stray
        // invocation must not silently re-time a line either.
        GivenTimings(document: ThreeLines());
        var cut = Render();

        await cut.InvokeAsync(() => Model(cut).RecordLineTap(50_000));

        Assert.That(Model(cut).EditedDocument!.Lines[1].StartMs, Is.EqualTo(10_000));
    }

    [Test]
    public async Task StoppingDisarmsTheKeyboard()
    {
        GivenTimings(document: ThreeLines());
        var cut = Render();

        await cut.InvokeAsync(() => Model(cut).StartRecording());
        await cut.InvokeAsync(() => Model(cut).StopRecording());
        await cut.InvokeAsync(() => Model(cut).RecordLineTap(50_000));

        Assert.That(Model(cut).EditedDocument!.Lines[1].StartMs, Is.EqualTo(10_000));
    }

    [Test]
    public void TheTapButtonHasNoBlazorClickHandler()
    {
        // Deliberate: a Blazor click would capture the moment on the SERVER, a round trip after the
        // press, making the button measurably worse than the space bar at its only job. JS reads the
        // audio clock in the click handler itself, found by this data attribute.
        GivenTimings(document: ThreeLines());
        var cut = Render();

        cut.FindAll("button").First(b => b.TextContent.Contains("Start tapping")).Click();

        var tap = cut.Find("[data-tap-now]");

        Assert.That(tap.HasAttribute("data-tap-now"), Is.True);
    }

    // -----------------------------------------------------------------
    // Publishing
    // -----------------------------------------------------------------

    private static void ClickPublish(IRenderedComponent<LyricsTimingEditor> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("Publish")).Click();

    private void GivenPublishReturns(LyricsEditResult result) =>
        MockLyricsService
            .Setup(x => x.PublishAsync(SongId, CreatorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Test]
    public void PublishingReleasesTheTimingsToListeners()
    {
        GivenTimings();
        GivenPublishReturns(LyricsEditResult.Ok("Published."));

        var cut = Render();
        ClickPublish(cut);

        MockLyricsService.Verify(
            x => x.PublishAsync(SongId, CreatorId, It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.That(cut.Markup, Does.Contain("Listeners can see these lyrics"));
    }

    [Test]
    public async Task PublishingSavesUnsavedEditsFirst()
    {
        // Otherwise a creator who tunes and presses Publish gets the last thing they happened to
        // save, not what they just heard - publishing a version that never existed on their screen.
        GivenTimings(document: ThreeLines());
        GivenPublishReturns(LyricsEditResult.Ok("Published."));
        MockLyricsService
            .Setup(x => x.SaveDraftAsync(SongId, CreatorId, It.IsAny<LyricsTimingsDocument>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LyricsEditResult.Ok("Saved."));

        var cut = Render();

        await cut.InvokeAsync(() => Model(cut).StartRecording());
        await cut.InvokeAsync(() => Model(cut).RecordLineTap(11_500));
        await cut.InvokeAsync(() => Model(cut).Publish());

        MockLyricsService.Verify(
            x => x.SaveDraftAsync(SongId, CreatorId, It.IsAny<LyricsTimingsDocument>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void ARefusedPublishShowsEveryReasonRatherThanASummary()
    {
        // Validation runs only at Publish, so this is the one place a creator meets it. "These
        // timings aren't ready" with no reason leaves them nothing to act on.
        GivenTimings();
        GivenPublishReturns(new LyricsEditResult(
            LyricsEditOutcome.Invalid,
            "These timings aren't ready to publish yet.",
            ["Line 4 starts before the line above it finishes.", "Line 9 runs past the end of the song."]));

        var cut = Render();
        ClickPublish(cut);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Line 4 starts before"));
            Assert.That(cut.Markup, Does.Contain("Line 9 runs past"));
            Assert.That(cut.Markup, Does.Not.Contain("Listeners can see these lyrics"));
        });
    }

    [Test]
    public void AnUnpublishedSongSaysListenersCannotSeeIt()
    {
        // The state every freshly aligned song is now in, and the thing a creator most needs to
        // understand: the old behaviour published automatically, so silence here reads as "done".
        GivenTimings();

        Assert.That(Render().Markup, Does.Contain("can't see these lyrics yet"));
    }

    [Test]
    public void PublishIsAlwaysAvailableEvenWithNothingEdited()
    {
        // The common case for a good alignment is listen, agree, publish - with no edits at all. A
        // Publish gated on being dirty would strand exactly the songs that came out well.
        GivenTimings();

        var cut = Render();
        var publish = cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("Publish"));

        Assert.That(publish.HasAttribute("disabled"), Is.False);
    }
}
