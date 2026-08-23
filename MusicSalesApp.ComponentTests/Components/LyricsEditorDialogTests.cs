using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Common.Contracts;
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

    /// <summary>
    /// A song whose timings landed has nothing left to do in this dialog.
    /// </summary>
    /// <remarks>
    /// Every action it used to offer has moved somewhere it belongs better: the export to the songs
    /// grid, the editor to Preview Results, and the re-run nowhere at all. A re-run costs another
    /// separation pass and comes back with the same inherent drift, so offering it read as advice to
    /// take it - and creators did, on timings that were already good.
    /// </remarks>
    [Test]
    public void ASongWithTimingsOffersNoRerunNoExportAndNoScore()
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
        cut.WaitForState(() => cut.Markup.Contains("Lyrics timed"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("Re-run timing"));
            Assert.That(cut.Markup, Does.Not.Contain("Time lyrics"));
            Assert.That(cut.Markup, Does.Not.Contain("Download .lrc"));
            Assert.That(cut.Markup, Does.Not.Contain("Fix the timing"));
            Assert.That(
                cut.Find(".e-msg-content").TextContent,
                Does.Not.Contain("94"),
                "The score is not shown.");
            Assert.That(cut.Markup, Does.Contain("Close"), "Closing is the only thing left.");
        });
    }

    /// <summary>
    /// Timings held for review say what is stopping them being seen, and nothing about a score.
    /// </summary>
    /// <remarks>
    /// The message used to quote the confidence. <c>NeedsReview</c> is where <em>every</em>
    /// successful alignment lands, at any score, and the score itself reads far worse than the
    /// timings deserve - so the number turned a neutral state into a verdict on the song. What the
    /// message owes the creator is the reason nothing is live yet, which is Publish.
    /// </remarks>
    [Test]
    public void TimingsHeldForReviewNameOnlyWhatIsStoppingThem()
    {
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
        cut.WaitForState(() => cut.Markup.Contains("until you press Publish"), TimeSpan.FromSeconds(5));

        // Asserted on the banner element, not the whole markup: SfDialog stamps a fresh GUID into
        // every id on the page, so "the markup does not contain 41" fails at random.
        var banner = cut.Find(".e-msg-content").TextContent;

        Assert.Multiple(() =>
        {
            Assert.That(banner, Does.Not.Contain("41"), "No score in the banner.");
            Assert.That(banner, Does.Not.Contain("confiden"));
            Assert.That(cut.Markup, Does.Not.Contain("Re-run timing"));
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

    /// <summary>
    /// A failed run is the one case that still offers to run the pipeline again.
    /// </summary>
    /// <remarks>
    /// This is the exception the whole change is built around. Re-running a <em>successful</em>
    /// alignment buys nothing - the same audio and the same words produce the same timings - but a
    /// failure usually means the pasted words were wrong for this track, and editing them and trying
    /// again is exactly the fix. Hence "Try again" rather than "Re-run timing".
    /// </remarks>
    [Test]
    public void AFailedRunStillOffersAnotherAttempt()
    {
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SongLyrics
            {
                SongMetadataId = 1,
                Status = SongLyricsStatus.Failed,
                Confidence = 0.41d
            });

        var cut = RenderDialog();
        cut.WaitForState(() => cut.Markup.Contains("Try again"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Try again"));
            Assert.That(
                cut.Find(".e-msg-content").TextContent,
                Does.Not.Contain("41"),
                "A stale score outlives a failure - never show it.");
        });
    }

    [Test]
    public void AnAttemptInFlightSaysItIsSafeToWalkAway()
    {
        // The run is started through Hangfire and finishes on a Durable Function that reports back
        // over HTTP - none of it touches this circuit - so a creator watching the bar is waiting for
        // no reason. That was always true and was stated nowhere.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetActiveJobAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LyricsAlignmentJob
            {
                JobId = Guid.NewGuid(),
                SongMetadataId = 1,
                Status = LyricsAlignmentJobStatus.Processing,
                Step = MusicSalesApp.Common.Contracts.LyricsAlignmentStep.SeparatingVocals
            });

        var cut = RenderDialog();
        cut.WaitForState(() => cut.Markup.Contains("Cancel timing"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("You can close this and carry on"));
            Assert.That(cut.Markup, Does.Contain("email you"));
        });
    }

    [Test]
    public void TheSafeToWalkAwayNoticeIsOnlyThereWhileSomethingIsRunning()
    {
        // Nothing is running, so there is nothing to reassure anybody about - and a standing notice
        // about work continuing in the background would be a lie in the state a creator most often
        // opens this dialog in.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);

        var cut = RenderDialog();

        Assert.That(cut.Markup, Does.Not.Contain("You can close this and carry on"));
    }

    [Test]
    public void ReplaceModeOffersThePasteBoxBackOnASongThatAlreadyHasTimings()
    {
        // The way back from a faithful alignment of the WRONG words - the one failure this feature
        // otherwise has no answer for, because nothing on Preview Results changes what the words are
        // and the paste box is hidden once timings exist.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SongLyrics
            {
                SongMetadataId = 1,
                Status = SongLyricsStatus.Published,
                TimingsBlobPath = "abc/abc-lyrics.json",
                LrcBlobPath = "abc/abc-lyrics.lrc"
            });

        var cut = RenderDialog(replacing: true);
        cut.WaitForState(() => cut.Markup.Contains("Replace and time"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Replace lyrics for"), "Named for what it is.");
            Assert.That(cut.Markup, Does.Contain("Replace and time"));
            Assert.That(
                cut.Markup,
                Does.Contain("including anything you have tapped, saved or published"),
                "Warned, because a replacement destroys the draft and the published timings.");
        });
    }

    [Test]
    public void WithoutReplaceModeATimedSongStillHasNoPasteBox()
    {
        // The guard rail on the above. Re-running the SAME words is what this feature deliberately
        // stopped offering, and replace mode must be a deliberate act by the host - not something a
        // creator falls into by reopening the dialog.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SongLyrics
            {
                SongMetadataId = 1,
                Status = SongLyricsStatus.Published,
                TimingsBlobPath = "abc/abc-lyrics.json"
            });

        var cut = RenderDialog();
        cut.WaitForState(() => cut.Markup.Contains("Lyrics timed"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("Replace and time"));
            Assert.That(cut.Markup, Does.Not.Contain("Time lyrics"));
        });
    }

    [Test]
    public void ASongWithNoTimingsYetHasNothingToEdit()
    {
        // Nothing has been aligned, so an editor button would lead to an empty page.
        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);

        var cut = RenderDialog();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("Fix the timing"));
            Assert.That(cut.Markup, Does.Contain("Time lyrics"));
        });
    }

    /// <summary>
    // -----------------------------------------------------------------
    // Where a finished attempt sends the creator
    // -----------------------------------------------------------------

    /// <summary>Stubs a job in flight, and what the song's state will be once it finishes.</summary>
    private Guid GivenAnAttemptInFlight(SongLyricsStatus finalStatus)
    {
        var jobId = Guid.NewGuid();

        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetActiveJobAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LyricsAlignmentJob
            {
                JobId = jobId,
                SongMetadataId = 1,
                CreatorId = 7,
                LyricsBlobPath = "abc/abc-lyrics.txt",
                Step = MusicSalesApp.Common.Contracts.LyricsAlignmentStep.Saving
            });

        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SongLyrics
            {
                SongMetadataId = 1,
                Status = finalStatus,
                TimingsBlobPath = finalStatus == SongLyricsStatus.Failed ? null : "abc/abc-lyrics.json"
            });

        return jobId;
    }

    /// <summary>
    /// Spin until a condition holds, or give up.
    /// </summary>
    /// <remarks>
    /// For state that changes OUTSIDE a render - navigation, specifically. bUnit's WaitForState only
    /// re-evaluates its predicate when the component renders again, so it cannot see something that
    /// happens after the last repaint.
    /// </remarks>
    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }
    }

    private void RaiseCompletion(Guid jobId) =>
        MockUploadProgressHubClient.Raise(
            x => x.OnLyricsProgress += null,
            new LyricsAlignmentProgress
            {
                JobId = jobId,
                Step = MusicSalesApp.Common.Contracts.LyricsAlignmentStep.Completed,
                OverallPercent = 100d
            });

    [Test]
    public void TimingThatLandsHandsTheSongBackToTheHost()
    {
        // The dialog reports; it does not decide. Leaving the creator in a paste box with nothing
        // left to paste was the old behaviour, and it made hearing the result an extra step most
        // people did not take - but which page to send them to is the host's call, because only the
        // host knows whether the creator is still there to be sent.
        var jobId = GivenAnAttemptInFlight(SongLyricsStatus.NeedsReview);

        int? handedBack = null;
        var cut = RenderDialog(EventCallback.Factory.Create<int>(this, id => handedBack = id));
        cut.WaitForState(() => cut.Markup.Contains("progress-bar"), TimeSpan.FromSeconds(5));

        var nav = TestContext.Services.GetRequiredService<NavigationManager>();
        var before = nav.Uri;
        RaiseCompletion(jobId);

        WaitUntil(() => handedBack is not null);

        Assert.Multiple(() =>
        {
            Assert.That(handedBack, Is.EqualTo(1), "The song whose timings landed.");
            Assert.That(nav.Uri, Is.EqualTo(before), "The dialog navigates nowhere itself.");
        });
    }

    [Test]
    public void TheHandoffDoesNotDependOnTheDialogStillBeingVisible()
    {
        // THE REGRESSION THIS EXISTS FOR. This used to be gated on IsVisible, and a creator who
        // watched the bar to the end was still left sitting in the paste box. IsVisible is a
        // parameter SfDialog also writes to through @bind-Visible, so it was never a trustworthy
        // answer to "is anyone still looking at this" - and the host, which is only rendered while
        // the creator is on the songs grid, answers the real question for free.
        var jobId = GivenAnAttemptInFlight(SongLyricsStatus.NeedsReview);

        int? handedBack = null;
        var cut = RenderDialog(EventCallback.Factory.Create<int>(this, id => handedBack = id));
        cut.WaitForState(() => cut.Markup.Contains("progress-bar"), TimeSpan.FromSeconds(5));

        cut.Render(parameters => parameters
            .Add(p => p.IsVisible, false)
            .Add(p => p.CreatorId, 7)
            .Add(p => p.Song, new SongAdminViewModel
            {
                Id = "1",
                SongTitle = "Night Drive",
                MediaGuid = Guid.Parse("abc00000-0000-0000-0000-000000000000")
            }));

        RaiseCompletion(jobId);
        WaitUntil(() => handedBack is not null);

        Assert.That(handedBack, Is.EqualTo(1));
    }

    [Test]
    public void AFailedAttemptStaysPutWhereTheMessageAndTheRetryAre()
    {
        var jobId = GivenAnAttemptInFlight(SongLyricsStatus.Failed);

        var cut = RenderDialog();
        cut.WaitForState(() => cut.Markup.Contains("progress-bar"), TimeSpan.FromSeconds(5));

        var nav = TestContext.Services.GetRequiredService<NavigationManager>();
        var before = nav.Uri;
        RaiseCompletion(jobId);

        cut.WaitForState(() => cut.Markup.Contains("Try again"), TimeSpan.FromSeconds(5));

        Assert.That(nav.Uri, Is.EqualTo(before), "A failure has somewhere to be, and it is here.");
    }


    /// <summary>
    /// A finished attempt clears the progress bar even when the parent's refresh fails.
    ///
    /// <para>
    /// The regression this exists for: the terminal handler used to clear <c>_isRunning</c>, then
    /// await the parent's <c>OnCompleted</c> grid reload, and only then repaint. A reload that threw
    /// therefore left the component believing it had finished while the DOM still showed the bar
    /// frozen at its last percent - and because the fallback poll stops once the attempt is no longer
    /// running, nothing ever came back to correct it. The creator watched 96% until they refreshed.
    /// </para>
    /// </summary>
    [Test]
    public void AFinishedAttemptClearsTheBarEvenIfTheParentRefreshThrows()
    {
        var jobId = Guid.NewGuid();

        MockLyricsService.SetupGet(x => x.IsAvailable).Returns(true);
        MockLyricsService.Setup(x => x.GetActiveJobAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LyricsAlignmentJob
            {
                JobId = jobId,
                SongMetadataId = 1,
                CreatorId = 7,
                LyricsBlobPath = "abc/abc-lyrics.txt",
                Step = MusicSalesApp.Common.Contracts.LyricsAlignmentStep.Saving
            });

        SetupRendererInfo();
        SetupAuthorizedUser(1, "creator@example.com", "Creator");

        // NOTE: the receiver here is this NUnit fixture, which is not an IHandleEvent - so
        // EventCallback.InvokeAsync calls the delegate directly and never goes through
        // ComponentBase.HandleEventAsync. That is the right shape for what this test asserts (a
        // throwing parent must not strand the dialog), but it is also why this fixture could never
        // have caught the dispatcher violation that shipped: production binds OnCompleted to a real
        // component. That path is covered by
        // CreatorSongManagementLyricsTests.ATerminalPushFromTheHubThreadStillRepaintsTheGrid.
        var cut = TestContext.Render<LyricsEditorDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.CreatorId, 7)
            .Add(p => p.Song, new SongAdminViewModel
            {
                Id = "1",
                SongTitle = "Night Drive",
                MediaGuid = Guid.Parse("abc00000-0000-0000-0000-000000000000")
            })
            .Add(p => p.OnCompleted, EventCallback.Factory.Create(this, () =>
                throw new InvalidOperationException("The song grid failed to reload."))));

        cut.WaitForState(() => cut.Markup.Contains("progress-bar"), TimeSpan.FromSeconds(5));

        // The attempt finishes. Only the terminal push arrives - the parent's reload then throws.
        MockUploadProgressHubClient.Raise(
            x => x.OnLyricsProgress += null,
            new LyricsAlignmentProgress
            {
                JobId = jobId,
                Step = MusicSalesApp.Common.Contracts.LyricsAlignmentStep.Completed,
                OverallPercent = 100d
            });

        cut.WaitForState(() => !cut.Markup.Contains("progress-bar"), TimeSpan.FromSeconds(5));

        Assert.That(
            cut.Markup,
            Does.Not.Contain("progress-bar"),
            "The bar must clear on the dialog's own repaint, not depend on the parent's reload surviving.");
    }

    private IRenderedComponent<LyricsEditorDialog> RenderDialog(
        EventCallback<int>? onTimingCompleted = null,
        bool replacing = false)
    {
        // SfDialog reads RendererInfo, which bUnit does not populate by default. The base fixture
        // exposes this helper precisely for dialog-hosting components.
        SetupRendererInfo();
        SetupAuthorizedUser(1, "creator@example.com", "Creator");

        return TestContext.Render<LyricsEditorDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.CreatorId, 7)
            .Add(p => p.OnTimingCompleted, onTimingCompleted ?? default)
            .Add(p => p.IsReplacing, replacing)
            .Add(p => p.Song, new SongAdminViewModel
            {
                Id = "1",
                SongTitle = "Night Drive",
                MediaGuid = Guid.Parse("abc00000-0000-0000-0000-000000000000")
            }));
    }
}
