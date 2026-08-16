using System.Security.Claims;
using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Creator;
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
    private void GivenLyricsFor(SongLyricsStatus status, double? confidence)
    {
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetForSongsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, SongLyrics>
            {
                [1] = new() { SongMetadataId = 1, Status = status, Confidence = confidence }
            });
    }

    [Test]
    public void TheConfidenceIsShownToOneDecimalPlace()
    {
        // To one decimal, unlike the dialog's whole-percent banner, because this column exists to
        // choose the threshold it is read against - and 69.6% and 70.4% both rounding to "70%" hides
        // exactly the distinction being made when they fall either side of the bar.
        GivenLyricsFor(SongLyricsStatus.Published, 0.5208d);

        var cut = TestContext.Render<CreatorSongManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading your songs"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("52.1"));
    }

    [Test]
    public void ASongHeldBackForReviewSaysSoAlongsideItsScore()
    {
        // The number alone does not say whether listeners can see the lyrics. Both halves matter:
        // the score is what the threshold gets judged against, the status is the consequence.
        GivenLyricsFor(SongLyricsStatus.NeedsReview, 0.4123d);

        var cut = TestContext.Render<CreatorSongManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading your songs"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("41.2"));
            Assert.That(cut.Markup, Does.Contain("Needs review"));
        });
    }

    [Test]
    public void ASongWithNoConfidenceYetStillRenders()
    {
        // Confidence is null for every song until an alignment completes, and null for a Failed one
        // permanently. A column that formatted it unguarded would throw during the grid's render,
        // which takes the whole song list with it - Edit and Delete included.
        GivenLyricsFor(SongLyricsStatus.Pending, null);

        var cut = TestContext.Render<CreatorSongManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading your songs"), TimeSpan.FromSeconds(5));

        Assert.That(ButtonLabels(cut), Does.Contain("Edit"));
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
