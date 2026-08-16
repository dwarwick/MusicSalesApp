using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The daily sweep for temp files a dead process left behind.
///
/// <para>
/// Two opposing risks, and the tests are mostly about the second. Deleting too little leaks an
/// abandoned batch onto shared hosting permanently, because nothing else sweeps a service account's
/// temp directory. Deleting too much reaches into a directory this process may not be the only writer
/// to - so the age rule and the name rule are what make it safe to run at all.
/// </para>
/// </summary>
[TestFixture]
public class TempFileCleanupServiceTests
{
    private string _directory = null!;
    private ILogger _logger = null!;
    private DateTime _now;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"sweeper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _logger = NullLogger.Instance;
        _now = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Writes a file and backdates it, since the age rule is the whole point.</summary>
    private string GivenFile(string name, TimeSpan age, string content = "x")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, _now - age);
        return path;
    }

    [Test]
    public void AFileAbandonedYesterday_IsDeleted()
    {
        var path = GivenFile("tmp1A2B.tmp", TimeSpan.FromDays(2));

        var result = TempFileCleanupService.Sweep(_directory, _now, _logger);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.False);
            Assert.That(result.Deleted, Is.EqualTo(1));
        });
    }

    [Test]
    public void AFileWrittenMinutesAgo_IsLeftAlone()
    {
        // The case that matters: a creator is sitting on the title-review step right now, with the
        // batch buffered. Sweeping it would destroy an upload in progress.
        var path = GivenFile("tmp1A2B.tmp", TimeSpan.FromMinutes(20));

        var result = TempFileCleanupService.Sweep(_directory, _now, _logger);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(result.Deleted, Is.Zero);
        });
    }

    [Test]
    public void AFileOrphanedByARecycleSurvivesTheFirstPass_AndIsSweptTheNextDay()
    {
        // This is why the job is scheduled rather than run at startup. Process start is the event
        // that orphans the file, but at that moment it is minutes old and the age rule - correctly -
        // refuses to touch it. Only a sweep that comes back later ever collects it.
        var path = GivenFile("tmp1A2B.tmp", TimeSpan.FromMinutes(5));

        TempFileCleanupService.Sweep(_directory, _now, _logger);
        Assert.That(File.Exists(path), Is.True, "Too new at the restart that stranded it.");

        var tomorrow = _now.AddDays(1);
        TempFileCleanupService.Sweep(_directory, tomorrow, _logger);
        Assert.That(File.Exists(path), Is.False, "Collected by a later run.");
    }

    [Test]
    public void AFileJustUnderTheThreshold_IsLeftAlone()
    {
        var path = GivenFile("tmp1A2B.tmp", TempFileCleanupService.MinimumAge - TimeSpan.FromMinutes(1));

        TempFileCleanupService.Sweep(_directory, _now, _logger);

        Assert.That(File.Exists(path), Is.True);
    }

    [Test]
    public void ItReportsWhatItReclaimed()
    {
        GivenFile("tmp1111.tmp", TimeSpan.FromDays(3), new string('a', 4096));
        GivenFile("tmp2222.tmp", TimeSpan.FromDays(3), new string('b', 2048));

        var result = TempFileCleanupService.Sweep(_directory, _now, _logger);

        Assert.Multiple(() =>
        {
            Assert.That(result.Deleted, Is.EqualTo(2));
            Assert.That(result.BytesReclaimed, Is.EqualTo(6144));
        });
    }

    // -----------------------------------------------------------------
    // Names. The glob is not the rule - it is only the first filter.
    // -----------------------------------------------------------------

    [Test]
    public void AnOldFileWithADeliberateName_IsLeftAlone()
    {
        // "tmp*.tmp" also matches this, and a temp directory can be shared with other applications.
        // Nothing is deleted unless its name could only have been generated, never chosen.
        var deliberate = GivenFile("tmp-nightly-export.tmp", TimeSpan.FromDays(90));
        var generated = GivenFile("tmpFFFF.tmp", TimeSpan.FromDays(90));

        var result = TempFileCleanupService.Sweep(_directory, _now, _logger);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(deliberate), Is.True, "A chosen name is somebody else's file.");
            Assert.That(File.Exists(generated), Is.False);
            Assert.That(result.Skipped, Is.EqualTo(1));
        });
    }

    [TestCase("tmp1A2B.tmp", true)]
    [TestCase("TMP1A2B.TMP", true, Description = "Windows paths are case-insensitive.")]
    [TestCase("tmpFFFF.tmp", true)]
    [TestCase("tmp1A2G.tmp", false, Description = "G is not a hex digit.")]
    [TestCase("tmp1A2.tmp", false, Description = "Three digits, so not a generated name.")]
    [TestCase("tmp1A2B3.tmp", false)]
    [TestCase("tmp1A2B.txt", false)]
    [TestCase("xyz1A2B.tmp", false)]
    [TestCase("tmp-report.tmp", false)]
    [TestCase("1A2B.tmp", false)]
    public void OnlyAGeneratedNameIsRecognised(string fileName, bool expected)
    {
        Assert.That(TempFileCleanupService.IsTempFileName(fileName), Is.EqualTo(expected));
    }

    // -----------------------------------------------------------------
    // It is a scheduled job, so a failure must not surface as one.
    // -----------------------------------------------------------------

    // Windows-only, and not an oversight. The behaviour under test is a *platform* guarantee: the
    // sweep leaves a locked file alone because FileInfo.Delete throws a sharing violation, which is
    // Windows semantics. POSIX unlink succeeds regardless of open handles, so on macOS or Linux the
    // file is deleted and this assertion cannot hold.
    //
    // Gated rather than rewritten, because the site runs on SmarterASP - an IIS-based Windows host -
    // so Windows is the only behaviour production ever sees, and there is no Unix path worth
    // asserting instead. Left ungated it fails permanently on a Mac, which is worse than useless:
    // a suite that is always one-red is a suite nobody reads carefully enough to notice two.
    [Test]
    [Platform("Win")]
    public void AFileSomethingStillHasOpen_IsLeftAloneWithoutThrowing()
    {
        // A lock is itself evidence the file is not abandoned, whatever its timestamp says.
        var path = GivenFile("tmp1A2B.tmp", TimeSpan.FromDays(30));

        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = TempFileCleanupService.Sweep(_directory, _now, _logger);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(result.Deleted, Is.Zero);
            Assert.That(result.Skipped, Is.EqualTo(1));
        });
    }

    [Test]
    public void AMissingDirectory_IsNotAnError()
    {
        var missing = Path.Combine(_directory, "does-not-exist");

        Assert.DoesNotThrow(() => TempFileCleanupService.Sweep(missing, _now, _logger));
    }

    [Test]
    public void AnEmptyDirectory_SweepsNothing()
    {
        var result = TempFileCleanupService.Sweep(_directory, _now, _logger);

        Assert.That(result.Deleted, Is.Zero);
    }

    [Test]
    public void Cancellation_StopsTheSweep()
    {
        GivenFile("tmp1111.tmp", TimeSpan.FromDays(3));
        GivenFile("tmp2222.tmp", TimeSpan.FromDays(3));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = TempFileCleanupService.Sweep(_directory, _now, _logger, cancelled.Token);

        Assert.That(result.Deleted, Is.Zero, "A shutdown mid-sweep must not keep deleting.");
    }

    [Test]
    public async Task TheJobEntryPointSweepsTheRealTempDirectoryWithoutThrowing()
    {
        // Hangfire calls this one, and an unhandled exception here would retry the job rather than
        // silently doing nothing. The directory it reaches is the real one, so this asserts only
        // that it survives - what it deletes is covered above.
        var service = new TempFileCleanupService(NullLogger<TempFileCleanupService>.Instance);

        await service.CleanupOrphanedTempFilesAsync();
    }

    [Test]
    public void TheJobIsScheduledDaily()
    {
        // A service nothing schedules is a service that never runs, and there is no other trigger -
        // no page visit, no startup hook.
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "MusicSalesApp", "Services", "BackgroundJobService.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("HangfireJobIds.CleanupOrphanedTempFiles"));
            Assert.That(source, Does.Contain("service.CleanupOrphanedTempFilesAsync()"));
            Assert.That(
                source,
                Does.Contain("\"10 7 * * *\""),
                "07:10 UTC is the slot clear of every other daily, hourly and */15 job.");
        });
    }

    [Test]
    public void TheJobIdIsStable()
    {
        // Hangfire keys recurring jobs by this string. Changing it leaves the old schedule in the
        // database firing a method that no longer exists.
        Assert.That(HangfireJobIds.CleanupOrphanedTempFiles, Is.EqualTo("cleanup-orphaned-temp-files"));
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MusicSalesApp", "MusicSalesApp.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
