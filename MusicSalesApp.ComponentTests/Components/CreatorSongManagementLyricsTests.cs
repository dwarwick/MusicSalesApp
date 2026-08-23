using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// Lyric timing is offered on the creator's song list only where it actually works.
///
/// <para>
/// The absence of <c>LyricsFunctions</c> configuration is the feature flag. An environment without
/// the Python Function app deployed is a perfectly normal environment - local development, and every
/// environment before that app is provisioned - not a creator having done something wrong. Showing
/// them a button whose only possible outcome is a dialog explaining why it does nothing is worse
/// than showing nothing.
/// </para>
/// </summary>
[TestFixture]
public class CreatorSongManagementLyricsTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        SetupRendererInfo();

        // The page is [Authorize(Policy = ManageOwnSongs)], so the policy has to be granted
        // explicitly - being authenticated is not enough, and without it the component renders
        // nothing at all and every assertion below fails for the wrong reason.
        var auth = AuthorizationContext;
        auth.SetAuthorized("creator@example.com");
        auth.SetClaims(
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Email, "creator@example.com"));
        auth.SetPolicies("ManageOwnSongs");

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(new ApplicationUser { Id = 1, Email = "creator@example.com" });

        MockCreatorService.Setup(x => x.GetCreatorIdForUserAsync(It.IsAny<int>()))
            .ReturnsAsync(7);
        MockCreatorService.Setup(x => x.GetCreatorSongsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<SongMetadata>
            {
                new()
                {
                    Id = 1,
                    SongTitle = "Night Drive",
                    CreatorId = 7,
                    Mp3BlobPath = "abc/abc-music.mp3",
                    IsActive = true,
                    IsEnabled = true
                }
            });

        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SongLyrics)null);
        MockLyricsService.Setup(x => x.GetActiveJobAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LyricsAlignmentJob)null);
        MockLyricsService.Setup(x => x.GetForSongsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, SongLyrics>());
        MockUploadProgressHubClient.Setup(x => x.StartAsync()).Returns(Task.CompletedTask);
    }

    /// <summary>
    /// The rendered button labels.
    ///
    /// <para>
    /// Asserted on rather than raw markup because the page heading is itself
    /// <c>&lt;i class="bi bi-music-note-list"&gt;My Songs&lt;/i&gt;</c> - searching the markup for
    /// that icon matches the title whether or not the button exists, which is a test that passes for
    /// the wrong reason and then fails for the wrong reason.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ButtonLabels(IRenderedComponent<CreatorSongManagement> cut)
        => cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();

    [Test]
    public void TheLyricsActionIsHiddenWhereTheFeatureIsNotConfigured()
    {
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(false);

        var cut = TestContext.Render<CreatorSongManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading your songs"), TimeSpan.FromSeconds(5));

        Assert.That(ButtonLabels(cut), Does.Not.Contain("Lyrics"));
    }

    [Test]
    public void TheLyricsActionAppearsOnceTheFeatureIsConfigured()
    {
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);

        var cut = TestContext.Render<CreatorSongManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading your songs"), TimeSpan.FromSeconds(5));

        Assert.That(ButtonLabels(cut), Does.Contain("Lyrics"));
    }

    [Test]
    public void TheEditAndDeleteActionsAreUnaffectedEitherWay()
    {
        // The flag governs lyric timing and nothing else. A misplaced condition that also hid the
        // song's own management actions would be a far worse regression than the one being guarded.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(false);

        var cut = TestContext.Render<CreatorSongManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading your songs"), TimeSpan.FromSeconds(5));

        var labels = ButtonLabels(cut);

        Assert.Multiple(() =>
        {
            Assert.That(labels, Does.Contain("Edit"), "Edit must still be offered.");
            Assert.That(labels, Does.Contain("Delete"), "Delete must still be offered.");
        });
    }

    [Test]
    public void NoSignalRConnectionIsOpenedWhereTheFeatureIsNotConfigured()
    {
        // The dialog subscribes to the upload progress hub on first render. Mounting it in an
        // environment that cannot use it would cost every creator a connection on every visit to
        // this page, for a feature that is switched off.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(false);

        var cut = TestContext.Render<CreatorSongManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading your songs"), TimeSpan.FromSeconds(5));

        MockUploadProgressHubClient.Verify(x => x.StartAsync(), Times.Never);
    }

    /// <summary>Stubs the bulk lookup with a single song's lyrics state.</summary>
    private void GivenLyricsFor(
        SongLyricsStatus status,
        double? confidence,
        string lrcBlobPath = "abc/abc-lyrics.lrc")
    {
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetForSongsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, SongLyrics>
            {
                [1] = new()
                {
                    SongMetadataId = 1,
                    Status = status,
                    Confidence = confidence,
                    LrcBlobPath = lrcBlobPath
                }
            });
    }

    /// <summary>Renders the grid and waits for the song list to arrive.</summary>
    private IRenderedComponent<CreatorSongManagement> RenderLoaded()
    {
        var cut = TestContext.Render<CreatorSongManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading your songs"), TimeSpan.FromSeconds(5));
        return cut;
    }

    [Test]
    public void NoScoreIsEverShownInTheGrid()
    {
        // The aligner's score is systematically pessimistic - it answers "did the aligner think it
        // did well", not "are these timings good" - so a column of percentages read as a verdict on
        // songs that were fine. The state is what a creator can act on; the number is not.
        GivenLyricsFor(SongLyricsStatus.Published, 0.5208d);

        var cut = RenderLoaded();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Published"));
            Assert.That(cut.Markup, Does.Not.Contain("52.1"));
        });
    }

    [Test]
    public void ASongNotYetPublishedSaysSoWithoutAScore()
    {
        // "Not published", not "Needs review". Every successful alignment lands in NeedsReview
        // regardless of score, so wording it as a problem would tell a creator with perfectly good
        // timings that something is wrong with them. What is true of all of them is only that no
        // listener can see them yet.
        GivenLyricsFor(SongLyricsStatus.NeedsReview, 0.4123d);

        var cut = RenderLoaded();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Timed"));
            Assert.That(cut.Markup, Does.Contain("Not published"));
            Assert.That(cut.Markup, Does.Not.Contain("Needs review"));
            Assert.That(cut.Markup, Does.Not.Contain("41.2"));
        });
    }

    [Test]
    public void ASongWithNoConfidenceYetStillRenders()
    {
        // Confidence is null for every song until an alignment completes, and null for a Failed one
        // permanently. Kept from when this column formatted that number: formatting it unguarded
        // threw during the grid's render, which took the whole song list with it.
        GivenLyricsFor(SongLyricsStatus.Pending, null);

        Assert.That(ButtonLabels(RenderLoaded()), Does.Contain("Edit"));
    }

    [Test]
    public void ATimedSongOffersTheExportAndThePreviewInsteadOfThePasteBox()
    {
        // The point of the change. Once an alignment has landed there is nothing left to paste, so
        // the slot that opened the paste dialog hands over the .lrc, and the creator's next move -
        // hearing the result - is the other button.
        GivenLyricsFor(SongLyricsStatus.NeedsReview, 0.52d);

        var cut = RenderLoaded();
        var labels = ButtonLabels(cut);

        Assert.Multiple(() =>
        {
            Assert.That(labels, Does.Contain("Download LRC"));
            Assert.That(labels, Does.Not.Contain("Lyrics"));
            Assert.That(cut.Markup, Does.Contain("Preview Results"));
            Assert.That(cut.Markup, Does.Not.Contain("Fix the timing"));
        });
    }

    [TestCase(SongLyricsStatus.Failed)]
    [TestCase(SongLyricsStatus.Pending)]
    public void ASongWithoutUsableTimingsKeepsThePasteBox(SongLyricsStatus status)
    {
        // Failed is what the paste box exists for now: the words themselves were most likely wrong,
        // which is the one thing running the pipeline again can fix. Pending has nothing yet.
        GivenLyricsFor(status, confidence: null);

        var labels = ButtonLabels(RenderLoaded());

        Assert.Multiple(() =>
        {
            Assert.That(labels, Does.Contain("Lyrics"));
            Assert.That(labels, Does.Not.Contain("Download LRC"));
        });
    }

    [Test]
    public void TimingsWithNoLrcFallBackToThePasteBoxRatherThanADeadButton()
    {
        // The LRC copy is allowed to fail without costing the timings, so "the alignment succeeded"
        // and "there is a file to hand over" are two different questions. A Download button that
        // silently does nothing is worse than not offering one.
        GivenLyricsFor(SongLyricsStatus.NeedsReview, 0.52d, lrcBlobPath: null);

        var cut = RenderLoaded();
        var labels = ButtonLabels(cut);

        Assert.Multiple(() =>
        {
            Assert.That(labels, Does.Contain("Lyrics"));
            Assert.That(labels, Does.Not.Contain("Download LRC"));
            Assert.That(cut.Markup, Does.Contain("Preview Results"), "The timings are still there.");
        });
    }

    // -----------------------------------------------------------------
    // The handoff to Preview Results
    // -----------------------------------------------------------------

    /// <summary>
    /// Raises the lyrics dialog's completion callback, exactly as a finished attempt does.
    /// </summary>
    /// <remarks>
    /// Driven through the real child component rather than by reflecting onto the page's handler, so
    /// the markup wiring is under test too - a callback that was never bound would otherwise pass.
    /// Not awaited: the handler deliberately dwells on the notice before moving, and these tests want
    /// to look at the notice while it stands.
    /// </remarks>
    private static void RaiseTimingCompleted(IRenderedComponent<CreatorSongManagement> cut, int songId)
    {
        var dialog = cut.FindComponent<LyricsEditorDialog>();
        _ = cut.InvokeAsync(() => dialog.Instance.OnTimingCompleted.InvokeAsync(songId));
    }

    [Test]
    public void ATimedSongSaysSoAndNamesWhereItIsTakingThem()
    {
        // The complaint this fixes: a creator watched the bar to the end, the dialog closed, and they
        // were left looking at the grid with nothing to say the run had finished or what to do next.
        GivenLyricsFor(SongLyricsStatus.NeedsReview, 0.52d);

        var cut = RenderLoaded();
        RaiseTimingCompleted(cut, 1);

        cut.WaitForState(() => cut.Markup.Contains("Your lyrics are timed"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Night Drive"), "Named, so it is clear which song.");
            Assert.That(cut.Markup, Does.Contain("Preview Results"), "And where they are going.");
            Assert.That(cut.Markup, Does.Contain("Publish"), "And what is still required of them.");
        });
    }

    [Test]
    public void PreviewResultsNowGoesStraightThere()
    {
        GivenLyricsFor(SongLyricsStatus.NeedsReview, 0.52d);

        var cut = RenderLoaded();
        RaiseTimingCompleted(cut, 1);
        cut.WaitForState(() => cut.Markup.Contains("Your lyrics are timed"), TimeSpan.FromSeconds(5));

        var nav = TestContext.Services.GetRequiredService<NavigationManager>();
        cut.Find("button.hero-secondary-cta").Click();

        Assert.That(nav.Uri, Does.Contain(AppPageRoutes.CreatorSongLyrics(1)));
    }

    [Test]
    public void NotJustNowLeavesThemOnTheGrid()
    {
        // The escape hatch. Preview Results is still one button away on the song's own row, so
        // declining costs them nothing - and being moved somewhere you just said no to is worse than
        // never having been offered.
        GivenLyricsFor(SongLyricsStatus.NeedsReview, 0.52d);

        var cut = RenderLoaded();
        RaiseTimingCompleted(cut, 1);
        cut.WaitForState(() => cut.Markup.Contains("Your lyrics are timed"), TimeSpan.FromSeconds(5));

        var nav = TestContext.Services.GetRequiredService<NavigationManager>();
        var before = nav.Uri;

        cut.FindAll("button").First(b => b.TextContent.Contains("Not just now")).Click();

        Assert.Multiple(() =>
        {
            Assert.That(nav.Uri, Is.EqualTo(before));
            Assert.That(cut.Markup, Does.Contain("Preview Results"), "Still reachable from the row.");
        });
    }

    [Test]
    public void AFailingLyricsLookupCostsTheColumnAndNotTheSongList()
    {
        // Lyric timing is supplementary to this page - the creator came to manage songs. When the
        // lookup threw, LoadSongsAsync died with it and the grid rendered empty: no songs, no Edit,
        // no Delete, and a page reporting the creator has nothing. Found by an existing test that
        // stubbed every other lyrics call but not this one.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetForSongsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is unreachable"));

        var cut = TestContext.Render<CreatorSongManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading your songs"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Night Drive"), "The song list must survive.");
            Assert.That(ButtonLabels(cut), Does.Contain("Edit"));
        });
    }
}
