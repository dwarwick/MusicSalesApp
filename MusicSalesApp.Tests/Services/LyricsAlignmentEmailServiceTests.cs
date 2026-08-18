#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The mail a creator gets when their lyric timing lands.
///
/// <para>
/// This is the <em>only</em> notification that reaches somebody who closed the tab, and closing the
/// tab is the expected behaviour rather than the exception - timing takes several minutes, and the
/// SignalR progress bar reaches nobody once the circuit is gone.
/// </para>
/// </summary>
[TestFixture]
public class LyricsAlignmentEmailServiceTests
{
    private const int CreatorId = 7;
    private const int SongId = 1;

    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<ICreatorService> _creators = null!;
    private Mock<IEmailService> _email = null!;
    private Mock<IAppSettingsService> _settings = null!;
    private LyricsAlignmentEmailService _service = null!;

    private string? _sentSubject;
    private string? _sentBody;
    private string? _sentTo;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"lyrics-email-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);

        _creators = new Mock<ICreatorService>();
        _creators.Setup(c => c.GetCreatorByIdAsync(CreatorId))
            .ReturnsAsync(new Creator
            {
                Id = CreatorId,
                User = new ApplicationUser { Id = 3, Email = "creator@example.com", EmailConfirmed = true }
            });

        _email = new Mock<IEmailService>();
        _email.Setup(e => e.GetAppBaseUrl()).Returns("https://streamtunes.net/");
        _email.Setup(e => e.GetEmailLogoHtml()).Returns("<img src='logo' />");
        _email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string to, string subject, string body) =>
            {
                _sentTo = to;
                _sentSubject = subject;
                _sentBody = body;
                return Task.FromResult(true);
            });

        _settings = new Mock<IAppSettingsService>();
        _settings.Setup(s => s.GetLyricsCompletionEmailsEnabledAsync()).ReturnsAsync(true);

        _service = new LyricsAlignmentEmailService(
            _factory,
            _creators.Object,
            _email.Object,
            _settings.Object,
            Mock.Of<ILogger<LyricsAlignmentEmailService>>());
    }

    private async Task<Guid> SeedAsync(
        LyricsAlignmentJobStatus status = LyricsAlignmentJobStatus.Completed,
        double? confidence = 0.52,
        string? failureMessage = null)
    {
        var jobId = Guid.NewGuid();

        await using var context = new AppDbContext(_options);
        context.SongMetadata.Add(new SongMetadata
        {
            Id = SongId,
            SongTitle = "Five Year Plan",
            CreatorId = CreatorId,
            Mp3BlobPath = "abc/abc-music.mp3",
            IsActive = true,
            IsEnabled = true
        });
        context.LyricsAlignmentJobs.Add(new LyricsAlignmentJob
        {
            JobId = jobId,
            SongMetadataId = SongId,
            CreatorId = CreatorId,
            Status = status,
            FailureMessage = failureMessage,
            LyricsBlobPath = "abc/abc-lyrics.txt"
        });
        context.SongLyrics.Add(new SongLyrics
        {
            SongMetadataId = SongId,
            LyricsBlobPath = "abc/abc-lyrics.txt",
            Status = status == LyricsAlignmentJobStatus.Completed
                ? SongLyricsStatus.NeedsReview
                : SongLyricsStatus.Failed,
            Confidence = confidence
        });
        await context.SaveChangesAsync();

        return jobId;
    }

    [Test]
    public async Task ASuccessfulRunSaysTheTimingsAreNotLiveYet()
    {
        // The single most important sentence in the whole feature. Until this release an alignment
        // that cleared the threshold published itself, so a creator who is told only "your lyrics are
        // timed" will reasonably assume listeners can already see them - and never press Publish.
        await _service.SendCompletionEmailAsync(await SeedAsync());

        Assert.Multiple(() =>
        {
            Assert.That(_sentBody, Does.Contain("aren't live yet").IgnoreCase);
            Assert.That(_sentBody, Does.Contain("Publish"));
        });
    }

    [Test]
    public async Task ASuccessfulRunLinksStraightToTheTimingEditor()
    {
        await _service.SendCompletionEmailAsync(await SeedAsync());

        Assert.Multiple(() =>
        {
            Assert.That(_sentBody, Does.Contain(AppPageRoutes.CreatorSongLyrics(SongId)));
            Assert.That(_sentBody, Does.Contain("https://streamtunes.net/creator/songs/1/lyrics"));
            Assert.That(_sentBody, Does.Not.Contain("net//"), "The base URL's trailing slash is trimmed.");
        });
    }

    [Test]
    public async Task TheSubjectAndBodyCarryTheSongTitle()
    {
        await _service.SendCompletionEmailAsync(await SeedAsync());

        Assert.Multiple(() =>
        {
            Assert.That(_sentSubject, Does.Contain("Five Year Plan"));
            Assert.That(_sentBody, Does.Contain("Five Year Plan"));
            Assert.That(_sentTo, Is.EqualTo("creator@example.com"));
        });
    }

    [Test]
    public async Task AFailedRunExplainsItselfAndSaysNothingChangedForListeners()
    {
        var jobId = await SeedAsync(
            LyricsAlignmentJobStatus.Failed,
            confidence: null,
            failureMessage: "None of these words could be found in the song.");

        await _service.SendCompletionEmailAsync(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(_sentSubject, Does.Contain("couldn't time"));
            Assert.That(_sentBody, Does.Contain("None of these words could be found"));
            Assert.That(_sentBody, Does.Contain("Nothing has changed for your listeners"));
        });
    }

    [Test]
    public async Task TheFailureBodyStillCarriesTheTitleEvenThoughTheFailurePathNeverLoadedIt()
    {
        // The completion service's FailAsync loads the job with no includes, to keep the callback
        // cheap. Loading it here rather than widening that query is what gets a title into this mail
        // without slowing down every failure.
        var jobId = await SeedAsync(LyricsAlignmentJobStatus.Failed, confidence: null);

        await _service.SendCompletionEmailAsync(jobId);

        Assert.That(_sentSubject, Does.Contain("Five Year Plan"));
    }

    [Test]
    public async Task NothingIsSentWhenTheSettingIsOff()
    {
        // Checked inside the job rather than before enqueuing, so switching it off drains what is
        // already queued instead of letting a backlog arrive after the decision to stop.
        _settings.Setup(s => s.GetLyricsCompletionEmailsEnabledAsync()).ReturnsAsync(false);

        await _service.SendCompletionEmailAsync(await SeedAsync());

        _email.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task NothingIsSentToAnUnconfirmedAddress()
    {
        _creators.Setup(c => c.GetCreatorByIdAsync(CreatorId))
            .ReturnsAsync(new Creator
            {
                Id = CreatorId,
                User = new ApplicationUser { Id = 3, Email = "creator@example.com", EmailConfirmed = false }
            });

        await _service.SendCompletionEmailAsync(await SeedAsync());

        _email.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task AVanishedJobIsAQuietNoOp()
    {
        // The song may have been deleted between the alignment finishing and Hangfire picking this
        // up, which cascades the attempt away. Throwing would retry a job that can never succeed.
        Assert.DoesNotThrowAsync(() => _service.SendCompletionEmailAsync(Guid.NewGuid()));

        _email.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ASendFailurePropagatesSoHangfireRetriesIt()
    {
        // Swallowing this would turn a mail server that is briefly unreachable into a creator who is
        // never told their song is ready.
        _email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));

        var jobId = await SeedAsync();

        Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendCompletionEmailAsync(jobId));
    }

    [Test]
    public async Task TheBodyCarriesTheUnsubscribeFooterAndTheLogo()
    {
        await _service.SendCompletionEmailAsync(await SeedAsync());

        Assert.Multiple(() =>
        {
            Assert.That(_sentBody, Does.Contain(AppPageRoutes.ManageAccount));
            Assert.That(_sentBody, Does.Contain("<img src='logo' />"));
        });
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
