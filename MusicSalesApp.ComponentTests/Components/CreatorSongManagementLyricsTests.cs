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
}
