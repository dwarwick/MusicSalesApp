using System.Text;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The draft/publish half of the lyrics service: the part that decides what listeners hear.
///
/// <para>
/// Two properties are worth more than all the others here.  A creator's unfinished edits must never
/// reach a listener, and pressing Publish must change what listeners see <em>and</em> be noticed by
/// browsers that have already cached the previous version.
/// </para>
/// </summary>
[TestFixture]
public class SongLyricsDraftAndPublishTests
{
    private const int CreatorId = 7;
    private const int OtherCreatorId = 8;
    private const int SongId = 1;

    private static readonly Guid SongGuid = Guid.Parse("abc00000-0000-0000-0000-000000000000");

    /// <summary>Asked of the same helper the service uses, rather than hardcoded.</summary>
    private static string DraftPath =>
        SongMediaPaths.ResolveLyricsDraftTimingsTarget(SongId, SongGuid, "abc/abc-music.mp3");

    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IAzureStorageService> _storage = null!;
    private Dictionary<string, string> _blobs = null!;
    private Mock<IAdminNotificationService> _adminNotifications = null!;
    private SongLyricsService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"lyrics-draft-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);
        _blobs = new Dictionary<string, string>(StringComparer.Ordinal);

        _storage = new Mock<IAzureStorageService>();

        // A tiny in-memory blob store, so the tests can assert on what was actually written rather
        // than merely that an upload happened. The round trip is the point: a publish that wrote a
        // document nothing could read back would pass a "was UploadAsync called" assertion.
        _storage
            .Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns((string path, Stream stream, string _) =>
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                _blobs[path] = reader.ReadToEnd();
                return Task.FromResult(path);
            });

        _storage
            .Setup(s => s.OpenReadAsync(It.IsAny<string>()))
            .Returns((string path) => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(_blobs.TryGetValue(path, out var body) ? body : string.Empty))));

        _storage
            .Setup(s => s.DeleteAsync(It.IsAny<string>()))
            .Returns((string path) => Task.FromResult(_blobs.Remove(path)));

        var durable = new Mock<IDurableTaskClient>();
        durable.SetupGet(client => client.IsConfigured).Returns(true);

        _adminNotifications = new Mock<IAdminNotificationService>();

        _service = new SongLyricsService(
            _factory,
            _storage.Object,
            durable.Object,
            new RecordingBackgroundJobClient(),
            _adminNotifications.Object,
            Mock.Of<ILogger<SongLyricsService>>());
    }

    private static LyricsTimingsDocument Document(long firstStart = 1_000) => new()
    {
        SongId = SongId,
        DurationMs = 100_000,
        Confidence = 0.52,
        Lines =
        [
            new LyricsTimedLine { Text = "[Chorus]" },
            new LyricsTimedLine
            {
                Text = "one two",
                StartMs = firstStart,
                EndMs = firstStart + 2_000,
                Words =
                [
                    new LyricsTimedWord { Text = "one", StartMs = firstStart, EndMs = firstStart + 1_000 },
                    new LyricsTimedWord { Text = "two", StartMs = firstStart + 1_000, EndMs = firstStart + 2_000 }
                ]
            }
        ]
    };

    private async Task SeedAsync(SongLyricsStatus status = SongLyricsStatus.NeedsReview, int version = 3)
    {
        await using var context = new AppDbContext(_options);
        context.SongMetadata.Add(new SongMetadata
        {
            Id = SongId,
            SongTitle = "Night Drive",
            ArtistName = "Nobody",
            CreatorId = CreatorId,
            MediaGuid = SongGuid,
            Mp3BlobPath = "abc/abc-music.mp3",
            IsActive = true,
            IsEnabled = true
        });
        context.SongLyrics.Add(new SongLyrics
        {
            SongMetadataId = SongId,
            LyricsBlobPath = "abc/abc-lyrics.txt",
            TimingsBlobPath = "abc/abc-lyrics.json",
            LrcBlobPath = "abc/abc-lyrics.lrc",
            Status = status,
            Confidence = 0.52,
            Version = version
        });
        await context.SaveChangesAsync();

        _blobs["abc/abc-lyrics.json"] = LyricsTimingsSerializer.Serialize(Document());
    }

    private async Task<SongLyrics> RowAsync()
    {
        await using var context = new AppDbContext(_options);
        return await context.SongLyrics.SingleAsync();
    }

    // -----------------------------------------------------------------
    // Ownership
    // -----------------------------------------------------------------

    [Test]
    public async Task AnotherCreatorCannotReadTheTimings()
    {
        // The page is gated on "is a creator", which is not the claim "owns this song".
        await SeedAsync();

        var result = await _service.GetEditableTimingsAsync(SongId, OtherCreatorId);

        Assert.That(result.Outcome, Is.EqualTo(LyricsEditOutcome.NotAllowed));
    }

    [Test]
    public async Task AnotherCreatorCannotPublish()
    {
        await SeedAsync();

        var result = await _service.PublishAsync(SongId, OtherCreatorId);

        Assert.Multiple(async () =>
        {
            Assert.That(result.Outcome, Is.EqualTo(LyricsEditOutcome.NotAllowed));
            Assert.That((await RowAsync()).Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
        });
    }

    [Test]
    public async Task AnotherCreatorCannotSaveOverADraft()
    {
        await SeedAsync();

        var result = await _service.SaveDraftAsync(SongId, OtherCreatorId, Document());

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(LyricsEditOutcome.NotAllowed));
            Assert.That(_blobs.Keys, Has.None.Contains("draft"));
        });
    }

    // -----------------------------------------------------------------
    // Drafts
    // -----------------------------------------------------------------

    [Test]
    public async Task SavingADraftLeavesTheLiveTimingsUntouched()
    {
        // The whole reason the draft is a separate blob. A creator halfway through re-tapping a
        // chorus must not be broadcasting that state to listeners.
        await SeedAsync(SongLyricsStatus.Published);
        var live = _blobs["abc/abc-lyrics.json"];

        await _service.SaveDraftAsync(SongId, CreatorId, Document(firstStart: 50_000));

        var row = await RowAsync();
        Assert.Multiple(() =>
        {
            Assert.That(_blobs["abc/abc-lyrics.json"], Is.EqualTo(live), "The live document must not move.");
            Assert.That(row.Status, Is.EqualTo(SongLyricsStatus.Published), "Still published, still the old file.");
            Assert.That(row.DraftTimingsBlobPath, Is.EqualTo(DraftPath));
            Assert.That(DraftPath, Does.EndWith("-lyrics.draft.json"));
            Assert.That(row.HasUnpublishedChanges, Is.True);
        });
    }

    [Test]
    public async Task TheDraftIsWhatTheEditorReadsBack()
    {
        await SeedAsync();
        await _service.SaveDraftAsync(SongId, CreatorId, Document(firstStart: 42_000));

        var result = await _service.GetEditableTimingsAsync(SongId, CreatorId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(LyricsEditOutcome.Success));
            Assert.That(result.HasUnpublishedChanges, Is.True);
            Assert.That(result.Document!.Lines[1].StartMs, Is.EqualTo(42_000));
        });
    }

    [Test]
    public async Task WithNoDraftTheEditorReadsTheAlignersOutput()
    {
        await SeedAsync();

        var result = await _service.GetEditableTimingsAsync(SongId, CreatorId);

        Assert.Multiple(() =>
        {
            Assert.That(result.HasUnpublishedChanges, Is.False);
            Assert.That(result.Document!.Lines[1].StartMs, Is.EqualTo(1_000));
        });
    }

    [Test]
    public async Task ReopeningAFreshlyPublishedSongDoesNotClaimUnpublishedChanges()
    {
        // THE REGRESSION THIS EXISTS FOR. Publishing keeps the draft blob and stamps it level with
        // the publish, so "there is a draft file" stays true forever afterwards - and the editor,
        // which used to read exactly that, greeted a creator who had just published with "these are
        // your unpublished changes" while the songs grid, one click away, correctly said there were
        // none. The editor now asks the same property the grid does.
        await SeedAsync();
        await _service.SaveDraftAsync(SongId, CreatorId, Document(firstStart: 42_000));
        await _service.PublishAsync(SongId, CreatorId);

        var result = await _service.GetEditableTimingsAsync(SongId, CreatorId);
        var row = await RowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.HasUnpublishedChanges, Is.False, "What the editor's banner reads.");
            Assert.That(row.HasUnpublishedChanges, Is.False, "What the songs grid reads.");
            Assert.That(
                result.Document!.Lines[1].StartMs,
                Is.EqualTo(42_000),
                "Still resumes from what was published, which is why the draft blob is kept.");
        });
    }

    [Test]
    public async Task EditingAfterAPublishClaimsUnpublishedChangesAgain()
    {
        // The other half: the fix must not simply stop reporting drafts. A creator who publishes and
        // then keeps tapping does have unpublished work, and needs telling.
        await SeedAsync();
        await _service.PublishAsync(SongId, CreatorId);
        await _service.SaveDraftAsync(SongId, CreatorId, Document(firstStart: 55_000));

        var result = await _service.GetEditableTimingsAsync(SongId, CreatorId);
        var row = await RowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.HasUnpublishedChanges, Is.True);
            Assert.That(row.HasUnpublishedChanges, Is.True, "And the grid agrees.");
        });
    }

    [Test]
    public async Task AnUnreadableDraftFallsBackToTheLiveTimingsRatherThanFailing()
    {
        // The creator loses unsaved work either way; this way they can carry on working instead of
        // meeting an error page about a file they never asked about.
        await SeedAsync();
        await _service.SaveDraftAsync(SongId, CreatorId, Document(firstStart: 42_000));
        _blobs[DraftPath] = "{ corrupt";

        var result = await _service.GetEditableTimingsAsync(SongId, CreatorId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(LyricsEditOutcome.Success));
            Assert.That(result.HasUnpublishedChanges, Is.False);
            Assert.That(result.Document!.Lines[1].StartMs, Is.EqualTo(1_000));
        });
    }

    [Test]
    public async Task ADraftIsSavedEvenWhenItIsMidEditAndSelfContradictory()
    {
        // Normalize repairs, Validate refuses, and only Publish validates. Refusing to save an untidy
        // document would lose a creator's work for being halfway through a record pass.
        await SeedAsync();

        var messy = Document();
        messy.Lines[1].StartMs = 9_000;
        messy.Lines[1].EndMs = 2_000;

        var result = await _service.SaveDraftAsync(SongId, CreatorId, messy);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task DiscardingADraftRemovesBothTheRowPointerAndTheBlob()
    {
        await SeedAsync();
        await _service.SaveDraftAsync(SongId, CreatorId, Document(firstStart: 42_000));

        await _service.DiscardDraftAsync(SongId, CreatorId);

        var row = await RowAsync();
        Assert.Multiple(() =>
        {
            Assert.That(row.DraftTimingsBlobPath, Is.Null);
            Assert.That(row.DraftUpdatedAt, Is.Null);
            Assert.That(row.HasUnpublishedChanges, Is.False);
            Assert.That(_blobs.ContainsKey(DraftPath), Is.False);
        });
    }

    // -----------------------------------------------------------------
    // Turning lyrics off
    // -----------------------------------------------------------------

    [Test]
    public async Task HidingKeepsTheTimingsAndTakesThemOffTheAir()
    {
        await SeedAsync();
        await _service.PublishAsync(SongId, CreatorId);

        var result = await _service.UnpublishAsync(SongId, CreatorId);
        var row = await RowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(row.Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
            Assert.That(row.IsVisibleToListeners, Is.False, "Gone from web and both apps.");
            Assert.That(row.TimingsBlobPath, Is.Not.Null, "And the work behind them survives.");
        });
    }

    [Test]
    public async Task HidingIsUndoneByPublishing()
    {
        // The whole point of it being the reversible one.
        await SeedAsync();
        await _service.PublishAsync(SongId, CreatorId);
        await _service.UnpublishAsync(SongId, CreatorId);

        var result = await _service.PublishAsync(SongId, CreatorId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(RowAsync().GetAwaiter().GetResult().IsVisibleToListeners, Is.True);
        });
    }

    [Test]
    public async Task RemovingLeavesTheSongAsThoughLyricsHadNeverBeenPasted()
    {
        await SeedAsync();

        var result = await _service.RemoveAsync(SongId, CreatorId);

        await using var context = new AppDbContext(_options);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(context.SongLyrics.Any(), Is.False, "The row is gone.");
            Assert.That(_blobs, Is.Empty, "And so are its artifacts.");
        });
    }

    [Test]
    public async Task RemovingRefusesForSomebodyElsesSong()
    {
        await SeedAsync();

        var result = await _service.RemoveAsync(SongId, OtherCreatorId);

        await using var context = new AppDbContext(_options);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(context.SongLyrics.Any(), Is.True, "Nothing was deleted.");
        });
    }

    [Test]
    public async Task AnAdminTakedownSurvivesTheCreatorTryingToPublish()
    {
        // THE WHOLE REASON THIS IS A COLUMN AND NOT A STATUS. An unpublish and a takedown look
        // identical in Status, and the creator can undo an unpublish - so a takedown recorded that
        // way would last exactly until they pressed Publish again.
        await SeedAsync();
        await _service.PublishAsync(SongId, CreatorId);

        await _service.SetAdminDisabledAsync(SongId, adminUserId: 99, disabled: true, "Wrong words.");

        var result = await _service.PublishAsync(SongId, CreatorId);
        var row = await RowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Outcome, Is.EqualTo(LyricsEditOutcome.AdminDisabled));
            Assert.That(row.IsVisibleToListeners, Is.False, "Still off the air.");
            Assert.That(row.DisabledReason, Is.EqualTo("Wrong words."));
        });
    }

    [Test]
    public async Task ATakenDownSongIsInvisibleEvenWhileItsStatusSaysPublished()
    {
        // The mobile mapper and the public blob whitelist both ask IsVisibleToListeners rather than
        // reading Status themselves, which is what makes a takedown reach a phone at all.
        await SeedAsync();
        await _service.PublishAsync(SongId, CreatorId);
        await _service.SetAdminDisabledAsync(SongId, adminUserId: 99, disabled: true);

        var row = await RowAsync();
        var timings = await _service.GetPublishedTimingsAsync(SongId);

        Assert.Multiple(() =>
        {
            Assert.That(row.Status, Is.EqualTo(SongLyricsStatus.Published), "Status is untouched.");
            Assert.That(row.IsVisibleToListeners, Is.False, "But nobody may see them.");
            Assert.That(timings, Is.Null, "Including the web player.");
        });
    }

    [Test]
    public async Task ReEnablingGivesTheDecisionBackWithoutMakingItForThem()
    {
        // Re-enabling restores the creator's ability to publish; it does not publish. Deciding for
        // them would put words in front of listeners the creator has not looked at since.
        await SeedAsync();
        await _service.PublishAsync(SongId, CreatorId);
        await _service.SetAdminDisabledAsync(SongId, adminUserId: 99, disabled: true, "Wrong words.");

        await _service.SetAdminDisabledAsync(SongId, adminUserId: 99, disabled: false);

        var row = await RowAsync();
        var republished = await _service.PublishAsync(SongId, CreatorId);

        Assert.Multiple(() =>
        {
            Assert.That(row.DisabledAt, Is.Null);
            Assert.That(row.DisabledReason, Is.Null, "The reason goes with it.");
            Assert.That(republished.Success, Is.True, "And the creator is in charge again.");
        });
    }

    [Test]
    public async Task DisablingBumpsTheVersionInBothDirections()
    {
        // The blob path never changes and is cached for a year, so a phone holding these timings has
        // no other way to notice either the takedown or the restore.
        await SeedAsync(version: 3);

        await _service.SetAdminDisabledAsync(SongId, adminUserId: 99, disabled: true);
        var afterDisable = (await RowAsync()).Version;

        await _service.SetAdminDisabledAsync(SongId, adminUserId: 99, disabled: false);
        var afterEnable = (await RowAsync()).Version;

        Assert.Multiple(() =>
        {
            Assert.That(afterDisable, Is.EqualTo(4));
            Assert.That(afterEnable, Is.EqualTo(5), "Restoring needs a bump as much as removing did.");
        });
    }

    // -----------------------------------------------------------------
    // Publishing
    // -----------------------------------------------------------------

    [Test]
    public async Task PublishingPromotesTheDraftOverTheLiveTimings()
    {
        await SeedAsync();
        await _service.SaveDraftAsync(SongId, CreatorId, Document(firstStart: 42_000));

        var result = await _service.PublishAsync(SongId, CreatorId);

        var published = LyricsTimingsSerializer.Deserialize(_blobs["abc/abc-lyrics.json"])!;
        var row = await RowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(published.Lines[1].StartMs, Is.EqualTo(42_000));
            Assert.That(row.Status, Is.EqualTo(SongLyricsStatus.Published));
            Assert.That(row.PublishedAt, Is.Not.Null);
            Assert.That(row.HasUnpublishedChanges, Is.False, "Publishing clears the indicator.");
        });
    }

    [Test]
    public async Task PublishingTellsAdminWhoPutLyricsInFrontOfListeners()
    {
        await SeedAsync();

        await _service.PublishAsync(SongId, CreatorId);

        _adminNotifications.Verify(
            n => n.NotifyLyricsPublishedAsync(CreatorId, SongId),
            Times.Once);
    }

    [Test]
    public async Task ARefusedPublishTellsAdminNothing()
    {
        await SeedAsync();

        var result = await _service.PublishAsync(SongId, OtherCreatorId);

        Assert.That(result.Success, Is.False);
        _adminNotifications.Verify(
            n => n.NotifyLyricsPublishedAsync(It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    [Test]
    public async Task AFailingAdminNotificationDoesNotFailThePublish()
    {
        // The publish is already committed by the time the notification runs, so letting it throw
        // would report "we couldn't publish" for lyrics that are, in fact, live to listeners.
        await SeedAsync();

        _adminNotifications
            .Setup(n => n.NotifyLyricsPublishedAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("smtp is down"));

        var result = await _service.PublishAsync(SongId, CreatorId);
        var row = await RowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(row.Status, Is.EqualTo(SongLyricsStatus.Published));
        });
    }

    [Test]
    public async Task PublishingBumpsTheVersionSoCachedBrowsersSeeTheChange()
    {
        // The blob path never changes and the response carries an immutable, year-long cache header.
        // Without the bump a re-publish is invisible to every returning browser, permanently.
        await SeedAsync(version: 3);

        await _service.PublishAsync(SongId, CreatorId);

        Assert.That((await RowAsync()).Version, Is.EqualTo(4));
    }

    [Test]
    public async Task PublishingRegeneratesTheLrcFromTheSameDocument()
    {
        // Otherwise the Download LRC button keeps handing out the timings from before the edit -
        // two files describing the same song differently, with nothing to say which is current.
        await SeedAsync();
        _blobs["abc/abc-lyrics.lrc"] = "[00:00.00]stale\n";

        await _service.PublishAsync(SongId, CreatorId, default);

        Assert.Multiple(() =>
        {
            Assert.That(_blobs["abc/abc-lyrics.lrc"], Does.Not.Contain("stale"));
            Assert.That(_blobs["abc/abc-lyrics.lrc"], Does.Contain("[00:01.00]"), "The line start.");
            Assert.That(_blobs["abc/abc-lyrics.lrc"], Does.Contain("[Chorus]"), "Untimed lines survive.");
        });
    }

    [Test]
    public async Task PublishingRefusesTimingsThatWouldDriftAndSaysWhyInPlainEnglish()
    {
        await SeedAsync();

        var broken = Document();
        broken.Lines.Add(new LyricsTimedLine
        {
            Text = "overlapping",
            StartMs = 1_500,
            EndMs = 4_000,
            Words = [new LyricsTimedWord { Text = "overlapping", StartMs = 1_500, EndMs = 4_000 }]
        });
        await _service.SaveDraftAsync(SongId, CreatorId, broken);

        var result = await _service.PublishAsync(SongId, CreatorId);

        Assert.Multiple(async () =>
        {
            Assert.That(result.Outcome, Is.EqualTo(LyricsEditOutcome.Invalid));
            Assert.That(result.Problems, Is.Not.Empty);
            Assert.That((await RowAsync()).Status, Is.EqualTo(SongLyricsStatus.NeedsReview), "Not published.");
        });
    }

    [Test]
    public async Task ASongWithNoTimingsCannotBePublished()
    {
        await using (var context = new AppDbContext(_options))
        {
            context.SongMetadata.Add(new SongMetadata
            {
                Id = SongId,
                CreatorId = CreatorId,
                MediaGuid = SongGuid,
                Mp3BlobPath = "abc/abc-music.mp3",
                IsActive = true,
                IsEnabled = true
            });
            context.SongLyrics.Add(new SongLyrics
            {
                SongMetadataId = SongId,
                LyricsBlobPath = "abc/abc-lyrics.txt",
                Status = SongLyricsStatus.Pending
            });
            await context.SaveChangesAsync();
        }

        var result = await _service.PublishAsync(SongId, CreatorId);

        Assert.That(result.Outcome, Is.EqualTo(LyricsEditOutcome.NoTimings));
    }

    [Test]
    public async Task PublishingWithNoDraftReleasesTheAlignersOwnTimings()
    {
        // The common case for a good alignment: the creator listens, agrees, and publishes without
        // changing anything.
        await SeedAsync();

        var result = await _service.PublishAsync(SongId, CreatorId);

        Assert.Multiple(async () =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That((await RowAsync()).Status, Is.EqualTo(SongLyricsStatus.Published));
        });
    }

    private sealed class RecordingBackgroundJobClient : IBackgroundJobClient
    {
        public string Create(Job job, IState state) => Guid.NewGuid().ToString("N");

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
