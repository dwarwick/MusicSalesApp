#nullable enable
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ReportedSongServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<IEmailService> _mockEmailService;
    private Mock<ISongStatusService> _mockSongStatusService;
    private Mock<ILogger<ReportedSongService>> _mockLogger;
    private ReportedSongService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;
    private SqliteConnection _connection;

    [SetUp]
    public void SetUp()
    {
        // SQLite rather than the in-memory provider, because the decision path claims a report with
        // a conditional UPDATE (ExecuteUpdateAsync) and the in-memory provider cannot run one. The
        // guard against two admins deciding the same report is the whole point of that statement,
        // so it has to be a provider that actually executes it. Held open for the fixture's
        // lifetime: an in-memory SQLite database exists only as long as a connection to it does.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(_contextOptions);
        _context.Database.EnsureCreated();

        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _mockEmailService = new Mock<IEmailService>();
        _mockEmailService.Setup(e => e.GetLogoUrl()).Returns("https://example.com/logo.png");
        _mockEmailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // The status service is mocked, so it does not write IsEnabled back to the in-memory
        // database. These tests assert that it was asked to, which is the contract that matters -
        // what disabling does to playlists and history is SongStatusServiceTests' business.
        _mockSongStatusService = new Mock<ISongStatusService>();
        _mockSongStatusService
            .Setup(s => s.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockSongStatusService
            .Setup(s => s.EnableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockLogger = new Mock<ILogger<ReportedSongService>>();

        _service = new ReportedSongService(
            _mockContextFactory.Object,
            _mockEmailService.Object,
            _mockSongStatusService.Object,
            _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task SeedSongAndUserAsync(int songId = 1, int userId = 10, int creatorUserId = 20, bool songEnabled = true)
    {
        var creatorUser = new ApplicationUser { Id = creatorUserId, UserName = "creator", Email = "creator@test.com" };
        var reportingUser = new ApplicationUser { Id = userId, UserName = "reporter", Email = "reporter@test.com" };
        // A song can only be reported once per listener, so duplicates need more listeners.
        var secondReporter = new ApplicationUser { Id = 11, UserName = "reporter2", Email = "reporter2@test.com" };
        var thirdReporter = new ApplicationUser { Id = 12, UserName = "reporter3", Email = "reporter3@test.com" };
        _context.Users.AddRange(creatorUser, reportingUser, secondReporter, thirdReporter);

        var creator = new Creator { Id = 1, UserId = creatorUserId, DisplayName = "Test Creator" };
        _context.Creators.Add(creator);

        _context.SongMetadata.Add(new SongMetadata
        {
            Id = songId,
            SongTitle = "Test Song",
            CreatorId = creator.Id,
            IsEnabled = songEnabled,
            Mp3BlobPath = "test.mp3"
        });

        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task ReportSongAsync_WithValidData_CreatesReport()
    {
        // Arrange
        await SeedSongAndUserAsync();

        // Act
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Assert
        Assert.That(report, Is.Not.Null);
        Assert.That(report.SongMetadataId, Is.EqualTo(1));
        Assert.That(report.ReportingUserId, Is.EqualTo(10));
        Assert.That(report.Reason, Is.EqualTo(ReportReasonTypes.CopyrightViolation));
        Assert.That(report.ResolutionAccepted, Is.Null);

        // Verify it was persisted
        using var verifyContext = new AppDbContext(_contextOptions);
        var saved = await verifyContext.ReportedSongs.FirstOrDefaultAsync();
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.SongMetadataId, Is.EqualTo(1));
    }

    [Test]
    public async Task ReportSongAsync_SendsThreeEmails()
    {
        // Arrange
        await SeedSongAndUserAsync();

        // Act
        await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Assert: admin + creator + reporter = 3 emails
        _mockEmailService.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(3));
    }

    [Test]
    public void ReportSongAsync_WithInvalidReason_Throws()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.ReportSongAsync(10, 1, "Invalid Reason"));
    }

    [Test]
    public async Task ReportSongAsync_WithNonExistentSong_Throws()
    {
        // Arrange: seed user but no song
        _context.Users.Add(new ApplicationUser { Id = 10, UserName = "reporter", Email = "reporter@test.com" });
        await _context.SaveChangesAsync();

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ReportSongAsync(10, 999, ReportReasonTypes.CopyrightViolation));
    }

    [Test]
    public async Task ReportSongAsync_DuplicateReport_Throws()
    {
        // Arrange
        await SeedSongAndUserAsync();
        await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ReportSongAsync(10, 1, ReportReasonTypes.TermsOfUseViolation));
        Assert.That(ex!.Message, Does.Contain("already reported"));
    }

    [Test]
    public async Task ReportSongAsync_DifferentUsersCanReportSameSong()
    {
        // Arrange
        var user2 = new ApplicationUser { Id = 30, UserName = "reporter2", Email = "reporter2@test.com" };
        await SeedSongAndUserAsync();
        _context.Users.Add(user2);
        await _context.SaveChangesAsync();

        // Act
        var report1 = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        var report2 = await _service.ReportSongAsync(30, 1, ReportReasonTypes.TermsOfUseViolation);

        // Assert
        Assert.That(report1.Id, Is.Not.EqualTo(report2.Id));

        using var verifyContext = new AppDbContext(_contextOptions);
        var count = await verifyContext.ReportedSongs.CountAsync();
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetAllReportsAsync_ReturnsAllReports()
    {
        // Arrange
        await SeedSongAndUserAsync();
        await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Act
        var reports = await _service.GetAllReportsAsync();

        // Assert
        Assert.That(reports, Has.Count.EqualTo(1));
        Assert.That(reports[0].SongMetadata, Is.Not.Null);
        Assert.That(reports[0].ReportingUser, Is.Not.Null);
    }

    [Test]
    public async Task ResolveReportAsync_Uphold_DisablesSongAndRecordsDecision()
    {
        // Arrange
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        // Act
        var result = await _service.ResolveReportAsync(report.Id, true, adminUserId: 99, baseUrl: "https://x/");

        // Assert
        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.SongDisabled, Is.True);

        _mockSongStatusService.Verify(
            s => s.DisableSongAsync(1, It.Is<string>(r => r.Contains(ReportReasonTypes.CopyrightViolation)), 99, "https://x/"),
            Times.Once);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.True);
        Assert.That(resolved.ResolutionDateTime, Is.Not.Null);
    }

    [Test]
    public async Task ResolveReportAsync_UpholdOnAlreadyDisabledSong_DoesNotDisableAgain()
    {
        // A second report on a song the first report already took down must not re-run the
        // takedown, or the creator is emailed the same news twice.
        await SeedSongAndUserAsync(songEnabled: false);
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.TermsOfUseViolation);

        var result = await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.SongDisabled, Is.False);
        _mockSongStatusService.Verify(
            s => s.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task ResolveReportAsync_Dismiss_LeavesSongAlone()
    {
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        var result = await _service.ResolveReportAsync(report.Id, false, 99, "https://x/");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.SongDisabled, Is.False);
        _mockSongStatusService.Verify(
            s => s.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.False);
    }

    [Test]
    public async Task ResolveReportAsync_EmailsAdminAndCreator()
    {
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        _mockEmailService.Invocations.Clear();

        await _service.ResolveReportAsync(report.Id, false, 99, "https://x/");

        _mockEmailService.Verify(
            e => e.SendEmailAsync("admin@streamtunes.net", It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
        _mockEmailService.Verify(
            e => e.SendEmailAsync("creator@test.com", It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task ResolveReportAsync_Uphold_LeavesCreatorMailToTheStatusService()
    {
        // DisableSongAsync already tells the creator, at far more length than this could.
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        _mockEmailService.Invocations.Clear();

        await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        _mockEmailService.Verify(
            e => e.SendEmailAsync("creator@test.com", It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _mockEmailService.Verify(
            e => e.SendEmailAsync("admin@streamtunes.net", It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task ResolveReportAsync_AlreadyDecided_RefusesToDecideAgain()
    {
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        await _service.ResolveReportAsync(report.Id, false, 99, "https://x/");

        var second = await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        Assert.That(second.Succeeded, Is.False);
        _mockSongStatusService.Verify(
            s => s.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.False, "the first decision stands");
    }

    [Test]
    public async Task ResolveReportAsync_WhenDisableFails_LeavesReportUndecided()
    {
        // Recording first would leave an upheld report sitting over a song still playing.
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        _mockSongStatusService
            .Setup(s => s.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));

        var result = await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        Assert.That(result.Succeeded, Is.False);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.Null);
        Assert.That(resolved.ResolutionDateTime, Is.Null);
    }

    [Test]
    public async Task ResolveReportAsync_NonExistentReport_ReturnsFalse()
    {
        var result = await _service.ResolveReportAsync(999, true, 99, "https://x/");

        Assert.That(result.Succeeded, Is.False);
    }

    [Test]
    public async Task ReverseDecisionAsync_AfterUphold_ReEnablesSongAndFlipsDecision()
    {
        // The appeal case: the song was taken down, the creator appealed and won.
        await SeedSongAndUserAsync(songEnabled: false);
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        var result = await _service.ReverseDecisionAsync(report.Id, "Valid licence supplied.", 99, "https://x/");

        Assert.That(result.Succeeded, Is.True);
        _mockSongStatusService.Verify(
            s => s.EnableSongAsync(1, It.Is<string>(r => r.Contains("Valid licence supplied.")), 99, "https://x/"),
            Times.Once);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.False);
        Assert.That(resolved.ResolutionDateTime, Is.Not.Null);
    }

    [Test]
    public async Task ReverseDecisionAsync_AfterDismiss_DisablesSong()
    {
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        await _service.ResolveReportAsync(report.Id, false, 99, "https://x/");

        var result = await _service.ReverseDecisionAsync(report.Id, "New information.", 99, "https://x/");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.SongDisabled, Is.True);
        _mockSongStatusService.Verify(
            s => s.DisableSongAsync(1, It.IsAny<string>(), 99, "https://x/"),
            Times.Once);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.True);
    }

    [Test]
    public async Task ReverseDecisionAsync_OnPendingReport_Refuses()
    {
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        var result = await _service.ReverseDecisionAsync(report.Id, "Because.", 99, "https://x/");

        Assert.That(result.Succeeded, Is.False);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.Null);
    }

    [Test]
    public async Task ReverseDecisionAsync_WithoutReason_Throws()
    {
        // The reason is the whole of what the creator is told about why their song moved.
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        Assert.ThrowsAsync<ArgumentException>(
            async () => await _service.ReverseDecisionAsync(report.Id, "   ", 99, "https://x/"));
    }

    [Test]
    public async Task ReverseDecisionAsync_CanBeReversedBackAgain()
    {
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        await _service.ReverseDecisionAsync(report.Id, "Appeal won.", 99, "https://x/");
        var second = await _service.ReverseDecisionAsync(report.Id, "Appeal was mistaken.", 99, "https://x/");

        Assert.That(second.Succeeded, Is.True);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.True);
    }

    [Test]
    public async Task ResolveReportAsync_ClosesOtherReportsOfTheSameThing()
    {
        // Three listeners reporting one song for one reason is one decision, not three.
        await SeedSongAndUserAsync();
        var first = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        var second = await _service.ReportSongAsync(11, 1, ReportReasonTypes.CopyrightViolation);
        var third = await _service.ReportSongAsync(12, 1, ReportReasonTypes.CopyrightViolation);

        var result = await _service.ResolveReportAsync(first.Id, true, 99, "https://x/");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Message, Does.Contain("2 other reports"));

        using var verifyContext = new AppDbContext(_contextOptions);
        foreach (var id in new[] { first.Id, second.Id, third.Id })
        {
            var row = await verifyContext.ReportedSongs.FindAsync(id);
            Assert.That(row!.ResolutionAccepted, Is.True, $"report {id}");
            Assert.That(row.ResolutionDateTime, Is.Not.Null, $"report {id}");
        }
    }

    [Test]
    public async Task ResolveReportAsync_DisablesTheSongOnceForAWholeGroup()
    {
        await SeedSongAndUserAsync();
        var first = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        await _service.ReportSongAsync(11, 1, ReportReasonTypes.CopyrightViolation);
        _mockEmailService.Invocations.Clear();

        await _service.ResolveReportAsync(first.Id, true, 99, "https://x/");

        _mockSongStatusService.Verify(
            s => s.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Once);
        _mockEmailService.Verify(
            e => e.SendEmailAsync("admin@streamtunes.net", It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task ResolveReportAsync_LeavesReportsOfSomethingElseOpen()
    {
        // Dismissing a terms-of-use complaint says nothing about a copyright one.
        await SeedSongAndUserAsync();
        var touReport = await _service.ReportSongAsync(10, 1, ReportReasonTypes.TermsOfUseViolation);
        var copyrightReport = await _service.ReportSongAsync(11, 1, ReportReasonTypes.CopyrightViolation);

        var result = await _service.ResolveReportAsync(touReport.Id, false, 99, "https://x/");

        Assert.That(result.Message, Does.Contain("still open"));

        using var verifyContext = new AppDbContext(_contextOptions);
        var untouched = await verifyContext.ReportedSongs.FindAsync(copyrightReport.Id);
        Assert.That(untouched!.ResolutionAccepted, Is.Null);
        Assert.That(untouched.ResolutionDateTime, Is.Null);
    }

    [Test]
    public async Task ResolveReportAsync_LeavesReportsAboutOtherSongsAlone()
    {
        await SeedSongAndUserAsync();
        _context.SongMetadata.Add(new SongMetadata
        {
            Id = 2,
            SongTitle = "Other Song",
            CreatorId = 1,
            IsEnabled = true,
            Mp3BlobPath = "other.mp3"
        });
        await _context.SaveChangesAsync();

        var target = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        var otherSong = await _service.ReportSongAsync(11, 2, ReportReasonTypes.CopyrightViolation);

        await _service.ResolveReportAsync(target.Id, true, 99, "https://x/");

        using var verifyContext = new AppDbContext(_contextOptions);
        var untouched = await verifyContext.ReportedSongs.FindAsync(otherSong.Id);
        Assert.That(untouched!.ResolutionDateTime, Is.Null);
    }

    [Test]
    public async Task ReverseDecisionAsync_FlipsTheWholeGroupBack()
    {
        // Otherwise an appeal re-enables the song while its duplicates still read "upheld".
        await SeedSongAndUserAsync();
        var first = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        var second = await _service.ReportSongAsync(11, 1, ReportReasonTypes.CopyrightViolation);
        await _service.ResolveReportAsync(first.Id, true, 99, "https://x/");

        await _service.ReverseDecisionAsync(second.Id, "Appeal won.", 99, "https://x/");

        using var verifyContext = new AppDbContext(_contextOptions);
        foreach (var id in new[] { first.Id, second.Id })
        {
            var row = await verifyContext.ReportedSongs.FindAsync(id);
            Assert.That(row!.ResolutionAccepted, Is.False, $"report {id}");
        }
    }

    [Test]
    public async Task ResolveReportAsync_ANewReportAfterADecisionIsStillItsOwnDecision()
    {
        // The group is swept at the moment of the decision, not retroactively - a complaint made
        // after the song came back is a fresh complaint.
        await SeedSongAndUserAsync();
        var first = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        await _service.ResolveReportAsync(first.Id, false, 99, "https://x/");

        var later = await _service.ReportSongAsync(11, 1, ReportReasonTypes.CopyrightViolation);

        using var verifyContext = new AppDbContext(_contextOptions);
        var row = await verifyContext.ReportedSongs.FindAsync(later.Id);
        Assert.That(row!.ResolutionDateTime, Is.Null);
    }

    [Test]
    public async Task ResolveReportAsync_WhenAnotherAdminDecidesFirst_DoesNothing()
    {
        // The claim is a conditional UPDATE precisely so that this is decided by the database and
        // not by a read that happened earlier. Stamping the row behind the service's back is what
        // the losing admin's request sees: it loads a report and finds the UPDATE matches nothing.
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        await using (var other = new AppDbContext(_contextOptions))
        {
            var row = await other.ReportedSongs.FindAsync(report.Id);
            row!.ResolutionAccepted = false;
            row.ResolutionDateTime = DateTime.UtcNow;
            await other.SaveChangesAsync();
        }

        var result = await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        Assert.That(result.Succeeded, Is.False);
        _mockSongStatusService.Verify(
            s => s.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);

        using var verifyContext = new AppDbContext(_contextOptions);
        var resolved = await verifyContext.ReportedSongs.FindAsync(report.Id);
        Assert.That(resolved!.ResolutionAccepted, Is.False, "the other admin's decision stands");
    }

    [Test]
    public async Task ReverseDecisionAsync_WhenAnotherUpheldReportStands_LeavesSongDisabled()
    {
        // Two different rules, judged separately. Winning the copyright appeal must not republish a
        // song that a live terms-of-use finding says has to stay off.
        await SeedSongAndUserAsync(songEnabled: false);
        var copyright = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        var termsOfUse = await _service.ReportSongAsync(11, 1, ReportReasonTypes.TermsOfUseViolation);
        await _service.ResolveReportAsync(copyright.Id, true, 99, "https://x/");
        await _service.ResolveReportAsync(termsOfUse.Id, true, 99, "https://x/");

        var result = await _service.ReverseDecisionAsync(copyright.Id, "Licence produced.", 99, "https://x/");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Message, Does.Contain("stays unavailable"));
        _mockSongStatusService.Verify(
            s => s.EnableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);

        using var verifyContext = new AppDbContext(_contextOptions);
        var reversed = await verifyContext.ReportedSongs.FindAsync(copyright.Id);
        Assert.That(reversed!.ResolutionAccepted, Is.False, "the appeal still succeeds on its own report");
        var stillUpheld = await verifyContext.ReportedSongs.FindAsync(termsOfUse.Id);
        Assert.That(stillUpheld!.ResolutionAccepted, Is.True);
    }

    [Test]
    public async Task ReverseDecisionAsync_WhenTheLastUpheldReportGoes_ReEnablesTheSong()
    {
        // The other half of the rule above: with nothing else holding it down, the song comes back.
        await SeedSongAndUserAsync(songEnabled: false);
        var copyright = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        var termsOfUse = await _service.ReportSongAsync(11, 1, ReportReasonTypes.TermsOfUseViolation);
        await _service.ResolveReportAsync(copyright.Id, true, 99, "https://x/");
        await _service.ResolveReportAsync(termsOfUse.Id, false, 99, "https://x/");

        var result = await _service.ReverseDecisionAsync(copyright.Id, "Licence produced.", 99, "https://x/");

        Assert.That(result.Succeeded, Is.True);
        _mockSongStatusService.Verify(
            s => s.EnableSongAsync(1, It.IsAny<string>(), 99, "https://x/"),
            Times.Once);
    }

    [Test]
    public async Task ResolveReportAsync_WhenTheSongVanishes_DoesNotClaimTheCreatorWasEmailed()
    {
        // DisableSongAsync returns false rather than throwing when the song row has gone. Treating
        // that as success would tell the admin the creator was emailed and suppress the one email
        // this service would otherwise have sent, so nobody would hear anything.
        await SeedSongAndUserAsync();
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        _mockSongStatusService
            .Setup(s => s.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        _mockEmailService.Invocations.Clear();

        var result = await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.SongDisabled, Is.False);
        Assert.That(result.Message, Does.Contain("no longer exists"));
        _mockEmailService.Verify(
            e => e.SendEmailAsync("creator@test.com", It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task ResolveReportAsync_WhenDisableFails_RestoresTheWholeGroup()
    {
        // The claim is taken before the song is touched, so a failure has to give it back - all of
        // it, or the duplicates stay stamped over a song that was never disabled.
        await SeedSongAndUserAsync();
        var first = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        var second = await _service.ReportSongAsync(11, 1, ReportReasonTypes.CopyrightViolation);
        _mockSongStatusService
            .Setup(s => s.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));

        var result = await _service.ResolveReportAsync(first.Id, true, 99, "https://x/");

        Assert.That(result.Succeeded, Is.False);

        using var verifyContext = new AppDbContext(_contextOptions);
        foreach (var id in new[] { first.Id, second.Id })
        {
            var row = await verifyContext.ReportedSongs.FindAsync(id);
            Assert.That(row!.ResolutionAccepted, Is.Null, $"report {id}");
            Assert.That(row.ResolutionDateTime, Is.Null, $"report {id}");
        }
    }

    [Test]
    public async Task ReverseDecisionAsync_WithAnOverlongReason_StillFitsTheStatusColumns()
    {
        // SongStatusHistory.Reason and SongMetadata.StatusReason are both MaxLength(1000). Over
        // that the status service throws, and the admin is told to try again - forever.
        await SeedSongAndUserAsync(songEnabled: false);
        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);
        await _service.ResolveReportAsync(report.Id, true, 99, "https://x/");

        var result = await _service.ReverseDecisionAsync(report.Id, new string('x', 4000), 99, "https://x/");

        Assert.That(result.Succeeded, Is.True);
        _mockSongStatusService.Verify(
            s => s.EnableSongAsync(It.IsAny<int>(), It.Is<string>(r => r.Length <= 1000), It.IsAny<int>(), It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task ReportSongAsync_ByTheSongsOwnCreator_IsRefusedAndWritesNothing()
    {
        // The queue exists to tell an admin what strangers object to. Left open, this is also a way
        // to have your own song disabled by whoever has borrowed your account, and a way to fill the
        // queue with rows no admin can act on.
        await SeedSongAndUserAsync();
        _mockEmailService.Invocations.Clear();

        Assert.ThrowsAsync<SelfReportNotAllowedException>(
            async () => await _service.ReportSongAsync(20, 1, ReportReasonTypes.CopyrightViolation));

        using var verifyContext = new AppDbContext(_contextOptions);
        Assert.That(await verifyContext.ReportedSongs.CountAsync(), Is.Zero);
        _mockEmailService.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task ReportSongAsync_ByAnyoneElse_IsStillAllowed()
    {
        // The guard is about the creator of THIS song, not about being a creator at all - somebody
        // who publishes music can still report somebody else's.
        await SeedSongAndUserAsync();

        var report = await _service.ReportSongAsync(10, 1, ReportReasonTypes.CopyrightViolation);

        Assert.That(report.ReportingUserId, Is.EqualTo(10));
    }

    [Test]
    public async Task ReportSongAsync_OnASongWithNoCreator_IsAllowed()
    {
        // A song whose creator row has gone reads as nobody's, which fails in the safe direction:
        // the report goes through and an admin sees it.
        await SeedSongAndUserAsync();
        _context.SongMetadata.Add(new SongMetadata
        {
            Id = 2,
            SongTitle = "Orphan Song",
            CreatorId = null,
            IsEnabled = true,
            Mp3BlobPath = "orphan.mp3"
        });
        await _context.SaveChangesAsync();

        var report = await _service.ReportSongAsync(20, 2, ReportReasonTypes.CopyrightViolation);

        Assert.That(report.SongMetadataId, Is.EqualTo(2));
    }
}
