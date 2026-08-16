using Bunit;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The creator's lyric-timing dialog.
///
/// <para>
/// Note what this can and cannot reach. The interesting behaviour after Submit is driven by SignalR
/// - a progress bar fed by an orchestration running in Azure - and bUnit cannot provide that, which
/// is the same limitation that makes <c>MockCoverArtMatchService.IsAvailable</c> default to false in
/// the base fixture. What is testable is everything before that point: whether the feature offers
/// itself at all, and whether the client-side guards stop a submission the server would reject.
/// </para>
/// </summary>
[TestFixture]
public class LyricsEditorDialogTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SongLyrics)null);
        MockLyricsService.Setup(x => x.GetActiveJobAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LyricsAlignmentJob)null);
        MockUploadProgressHubClient.Setup(x => x.StartAsync()).Returns(Task.CompletedTask);
    }

    [Test]
    public void AnEnvironmentWithoutTheFunctionAppSaysSoInsteadOfOfferingTheFeature()
    {
        // Not an error state. A site with no lyrics Function app configured simply does not offer
        // lyric timing, and everything else about it carries on working.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(false);

        var cut = RenderDialog();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("isn't configured for this environment"));
            Assert.That(cut.Markup, Does.Not.Contain("Time lyrics"));
        });
    }

    [Test]
    public void AConfiguredEnvironmentOffersTheEditor()
    {
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);

        var cut = RenderDialog();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Time lyrics"));
            Assert.That(cut.Markup, Does.Contain("Paste the lyrics"));
        });
    }

    [Test]
    public void TheCountersShowTheServerSideLimits()
    {
        // The caps are enforced server-side regardless; showing them is what stops a creator pasting
        // a novel and only finding out after a round trip.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);

        var cut = RenderDialog();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain(LyricsTextLimits.MaxCharacters.ToString("N0")));
            Assert.That(cut.Markup, Does.Contain(LyricsTextLimits.MaxLines.ToString("N0")));
        });
    }

    [Test]
    public void SectionHeadingsAreExplainedRatherThanLeftToGuesswork()
    {
        // Worth its own assertion: creators strip these out to be helpful, and doing so removes the
        // one thing that reliably raises a low confidence score.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);

        var cut = RenderDialog();

        Assert.That(cut.Markup, Does.Contain("[Chorus]"));
    }

    [Test]
    public void ASongWithPublishedTimingsOffersARerunAndAnExport()
    {
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SongLyrics
            {
                SongMetadataId = 1,
                Status = SongLyricsStatus.Published,
                Confidence = 0.94d,
                TimingsBlobPath = "abc/abc-lyrics.json",
                LrcBlobPath = "abc/abc-lyrics.lrc"
            });

        var cut = RenderDialog();
        cut.WaitForState(() => cut.Markup.Contains("Re-run timing"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Re-run timing"), "Not 'Time lyrics' - it has been done once.");
            Assert.That(cut.Markup, Does.Contain("Download .lrc"));
            Assert.That(cut.Markup, Does.Contain("94"), "The confidence is shown, not just the status.");
        });
    }

    [Test]
    public void ALowConfidenceResultExplainsItselfAndKeepsTheExport()
    {
        // The creator's most likely next question is "why", and the most useful answer is the one
        // they can act on: something in the pasted text that nobody sings.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SongLyrics
            {
                SongMetadataId = 1,
                Status = SongLyricsStatus.NeedsReview,
                Confidence = 0.41d,
                TimingsBlobPath = "abc/abc-lyrics.json",
                LrcBlobPath = "abc/abc-lyrics.lrc"
            });

        var cut = RenderDialog();
        cut.WaitForState(() => cut.Markup.Contains("aren't confident"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("won't be shown to listeners yet"));
            Assert.That(cut.Markup, Does.Contain("Download .lrc"), "Low-confidence timings are kept, not discarded.");
        });
    }

    [Test]
    public void AFailedAttemptInvitesAnotherTry()
    {
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SongLyrics { SongMetadataId = 1, Status = SongLyricsStatus.Failed });

        var cut = RenderDialog();
        cut.WaitForState(() => cut.Markup.Contains("couldn't time these lyrics"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("try again"));
    }

    [Test]
    public void AnAttemptAlreadyRunningShowsProgressAndOffersCancelInsteadOfSubmit()
    {
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetActiveJobAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LyricsAlignmentJob
            {
                JobId = Guid.NewGuid(),
                SongMetadataId = 1,
                CreatorId = 7,
                LyricsBlobPath = "abc/abc-lyrics.txt",
                Step = MusicSalesApp.Common.Contracts.LyricsAlignmentStep.SeparatingVocals
            });

        var cut = RenderDialog();
        cut.WaitForState(() => cut.Markup.Contains("Cancel timing"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("progress-bar"));
            Assert.That(cut.Markup, Does.Contain("Isolating the vocal"), "The slow stage says it is the slow stage.");
            Assert.That(cut.Markup, Does.Not.Contain("Time lyrics"), "Submit is replaced while one is running.");
        });
    }

    private IRenderedComponent<LyricsEditorDialog> RenderDialog()
    {
        // SfDialog reads RendererInfo, which bUnit does not populate by default. The base fixture
        // exposes this helper precisely for dialog-hosting components.
        SetupRendererInfo();
        SetupAuthorizedUser(1, "creator@example.com", "Creator");

        return TestContext.Render<LyricsEditorDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.CreatorId, 7)
            .Add(p => p.Song, new SongAdminViewModel
            {
                Id = "1",
                SongTitle = "Night Drive",
                MediaGuid = Guid.Parse("abc00000-0000-0000-0000-000000000000")
            }));
    }
}
