using MusicSalesApp.Components.Pages.Creator;

namespace MusicSalesApp.Tests.Components;

/// <summary>
/// The audio half of direct-to-storage upload: the browser PUTs a 57 MB file straight into Azure and
/// the web server is not in the transfer at all.
///
/// <para>
/// Almost everything that can go wrong here is invisible in every log this application owns, because
/// the request never reaches anything of ours - so what these pin is the handful of decisions made on
/// our side of that gap, and the ordering rules that only bite in production.
/// </para>
/// </summary>
public class UploadFilesDirectAudioTests
{
    // -----------------------------------------------------------------
    // Progress routing. Several phases report through one callback.
    // -----------------------------------------------------------------

    [Test]
    public void EachPhasesProgressReachesOnlyItsOwnBar()
    {
        // One phase per song on the audio path, up to four at once, and every one of them reports
        // through the same [JSInvokable]. Before the routing existed, one bar was written by all of
        // them - the songs would have overwritten each other and the image phase's batch figure.
        var page = new TestableUploadFiles();
        var songOne = new List<int>();
        var songTwo = new List<int>();

        page.RegisterPhase("audio-one", (_, percents) => songOne.Add(percents[0]));
        page.RegisterPhase("audio-two", (_, percents) => songTwo.Add(percents[0]));

        page.ReportUploadProgress("audio-one", [3], [40]);
        page.ReportUploadProgress("audio-two", [7], [90]);

        Assert.Multiple(() =>
        {
            Assert.That(songOne, Is.EqualTo(new[] { 40 }));
            Assert.That(songTwo, Is.EqualTo(new[] { 90 }));
        });
    }

    [Test]
    public void ProgressForAPhaseThatHasAlreadyFinished_IsDropped()
    {
        // Real, not theoretical: the module flushes on a timer and once more on the way out, so a
        // report can arrive after its phase completed and its router was removed. Falling back to
        // "write the batch bar" would drag a finished bar backwards.
        var page = new TestableUploadFiles();

        Assert.DoesNotThrow(() => page.ReportUploadProgress("audio-gone", [1], [50]));
    }

    [Test]
    public void AMalformedReport_IsIgnoredRatherThanThrowing()
    {
        // This crosses a JS boundary, and an exception on a [JSInvokable] surfaces on the circuit.
        var page = new TestableUploadFiles();
        var reports = 0;
        page.RegisterPhase("audio-one", (_, _) => reports++);

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => page.ReportUploadProgress("audio-one", null, [50]));
            Assert.DoesNotThrow(() => page.ReportUploadProgress("audio-one", [], []));
            Assert.That(reports, Is.Zero);
        });
    }

    // -----------------------------------------------------------------
    // The image phase's batch figure.
    // -----------------------------------------------------------------

    [Test]
    public void TheImageBarAveragesOverEveryFile_NotOverWhateverMovedLastTick()
    {
        // The flush carries only files that changed since the previous one. Averaging it directly is
        // the bug this guards: three images finished and one just starting reports 4%, so the bar
        // jumps backwards in front of the creator.
        var running = new Dictionary<int, int> { [0] = 100, [1] = 100, [2] = 100, [3] = 0 };

        var average = UploadFilesModel.ApplyImagePhaseFlush(running, [3], [4]);

        Assert.That(average, Is.EqualTo(76d).Within(0.001), "(100 + 100 + 100 + 4) / 4");
    }

    [Test]
    public void FilesThatHaveNotStartedStillCountAgainstTheAverage()
    {
        // Seeded at zero when the phase starts. Without that the first flush of a four-image phase
        // would average over one file and show 100% while three had not begun.
        var running = new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0, [3] = 0 };

        var average = UploadFilesModel.ApplyImagePhaseFlush(running, [0], [100]);

        Assert.That(average, Is.EqualTo(25d).Within(0.001));
    }

    [Test]
    public void APercentageOutsideTheRange_IsClamped()
    {
        var running = new Dictionary<int, int>();

        var average = UploadFilesModel.ApplyImagePhaseFlush(running, [0, 1], [-20, 900]);

        Assert.That(average, Is.EqualTo(50d).Within(0.001));
    }

    [Test]
    public void AnEmptyPhaseReportsZeroRatherThanDividingByZero()
    {
        Assert.That(UploadFilesModel.ApplyImagePhaseFlush([], [], []), Is.Zero);
    }

    // -----------------------------------------------------------------
    // Ordering and cleanup rules that only bite against real storage.
    // -----------------------------------------------------------------

    [Test]
    public void TheAudioNeverTouchesTheWebServersDiskOnTheDirectPath()
    {
        // The entire point of the change. A 24-song batch is ~1.4 GB that used to be written to a
        // shared host's temp disk, in full, before a byte reached Azure.
        var codeBehind = ReadCodeBehind();

        Assert.Multiple(() =>
        {
            Assert.That(
                codeBehind,
                Does.Contain("if (receivesAudio)"),
                "Buffering audio to a temp file must be skipped when the browser uploads it directly.");

            Assert.That(
                codeBehind,
                Does.Contain("CreateFromStagedAsync"),
                "The direct path must create the job from the staged blob, not from a stream.");
        });
    }

    [Test]
    public void TheBatchesCoverArtIsNotDeletedBeforeTheSongsThatNeedItExist()
    {
        // The ordering trap. On the direct path the images in batch/{id}/ ARE the cover art - nothing
        // else holds a copy - and each is copied into its song's folder only when that song is
        // created, which is after the review pause and after its audio has uploaded. Deleting on
        // pairing, as the server path does, silently publishes the whole batch with no art.
        var codeBehind = ReadCodeBehind();

        var sweepDefinition = codeBehind.IndexOf(
            "private async Task SweepPendingImageBatchAsync()", StringComparison.Ordinal);
        var uploadsComplete = codeBehind.IndexOf(
            "await ProcessUploadsInChunksAsync(uploads);", StringComparison.Ordinal);
        var sweepAfterUploads = codeBehind.IndexOf(
            "await SweepPendingImageBatchAsync();", uploadsComplete, StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(sweepDefinition, Is.GreaterThan(-1));
            Assert.That(uploadsComplete, Is.GreaterThan(-1));
            Assert.That(
                sweepAfterUploads,
                Is.GreaterThan(uploadsComplete),
                "The batch folder must be swept after the songs are created, never before.");

            Assert.That(
                codeBehind,
                Does.Contain("_pendingImageBatchId = batchId"),
                "The direct path must defer the delete rather than firing it on pairing.");
        });
    }

    [Test]
    public void AnAudioUploadThatNeverBecameAJob_HasItsBlobsDeleted()
    {
        // No row references the folder, so the reconciler cannot see it and the Function was never
        // told about it. On the server path this could not happen: the upload and the row were one
        // operation.
        var codeBehind = ReadCodeBehind();

        Assert.That(
            codeBehind,
            Does.Contain("DeleteStagedBlobsAsync(mediaGuid, CancellationToken.None)"),
            "An abandoned direct upload must clean up after itself.");
    }

    [Test]
    public void NavigatingAwayTellsTheBrowserToStop_NotJustTheServer()
    {
        // Cancelling the token reaches nothing: the bytes are moving from the creator's machine to
        // Azure with this server not in the path. Without the abort the browser cheerfully finishes
        // uploading 1.4 GB for a page that is no longer on screen.
        var codeBehind = ReadCodeBehind();

        var navigation = codeBehind.IndexOf(
            "protected async Task OnBeforeInternalNavigation", StringComparison.Ordinal);
        var abortAfterNavigation = codeBehind.IndexOf(
            "await AbortDirectUploadsAsync();", navigation, StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(navigation, Is.GreaterThan(-1));
            Assert.That(
                abortAfterNavigation,
                Is.GreaterThan(navigation),
                "Confirming a navigation away must abort the browser's uploads.");
        });
    }

    [Test]
    public void TheBrowsersConcurrencyLimitIsLowerThanTheServers()
    {
        // Not the same number wearing two hats. ChunkSize bounds transfers leaving a datacentre;
        // this bounds transfers leaving a creator's house, where eight concurrent 57 MB PUTs divide
        // one uplink eight ways and every bar crawls.
        var codeBehind = ReadCodeBehind();

        Assert.Multiple(() =>
        {
            Assert.That(codeBehind, Does.Contain("BrowserUploadConcurrency = 4"));
            Assert.That(codeBehind, Does.Contain("ChunkSize = 8"));
            Assert.That(
                codeBehind,
                Does.Contain("\"uploadFiles\", cancellationToken, phaseId, items, BrowserUploadConcurrency"),
                "The browser must be handed its own limit, not the server's.");
        });
    }

    /// <summary>
    /// Reaches the callback surface the browser drives. Nothing here renders or touches an injected
    /// service, so the component needs no DI graph.
    /// </summary>
    private sealed class TestableUploadFiles : UploadFilesModel
    {
        public void RegisterPhase(string phaseId, Action<int[], int[]> router)
            => _uploadProgressRouters[phaseId] = router;
    }

    private static string ReadCodeBehind()
        => File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs"));

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
