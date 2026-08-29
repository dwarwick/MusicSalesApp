using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.Services;
using SkiaSharp;

namespace MusicSalesApp.Tests.Components;

/// <summary>
/// Adding cover art to one song at the review step.
///
/// <para>
/// Until this existed, artwork could only enter a batch in the original file selection. A creator who
/// forgot an image, or whose image the matcher paired with the wrong song, had two options: abandon
/// the batch and re-select every file, or publish without artwork and fix it later. Both are worse
/// than browsing for the file from the row that needs it.
/// </para>
///
/// <para>
/// The invariant from <see cref="UploadFilesCoverArtRepairTests"/> still governs everything here -
/// <b>each image is either assigned to exactly one song or sitting in the pool, never both, never
/// neither</b> - and a browsed image joins that population rather than sitting outside it. What is new
/// is that the image is not in the browser's FileList, so it has to be made reachable by whichever
/// upload path its row is on before the row is allowed to point at it.
/// </para>
/// </summary>
[TestFixture]
public class UploadFilesRowCoverArtTests
{
    private const string StagedBlobPath = "batch/abc/7.png";

    private Mock<IUploadStagingSasService> _staging = null!;
    private List<(Guid BatchId, int Index, string Extension)> _staged = null!;

    // Pages are locals rather than a field: the component is IAsyncDisposable, and its DisposeAsync
    // reaches for injected services these tests deliberately do not supply. Kept here only so the
    // temp files a batch buffers can be cleaned up afterwards.
    private List<TestableUploadFiles> _pages = null!;

    [SetUp]
    public void SetUp()
    {
        _staged = [];
        _pages = [];

        _staging = new Mock<IUploadStagingSasService>();
        _staging
            .Setup(service => service.StageMatchImageAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback((Guid batchId, int index, string extension, Stream _, CancellationToken _) =>
                _staged.Add((batchId, index, extension)))
            .ReturnsAsync(StagedBlobPath);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var page in _pages)
        {
            page.DeleteTempFiles();
        }
    }

    private TestableUploadFiles NewPage()
    {
        var page = new TestableUploadFiles(_staging.Object);
        _pages.Add(page);
        return page;
    }

    // -----------------------------------------------------------------
    // The case the feature exists for.
    // -----------------------------------------------------------------

    [Test]
    public async Task BrowsingForArtOnASongThatHasNone_GivesItThatArt()
    {
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);

        await page.Browse(song, FakeImage("sleeve.png"));

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtError, Is.Null, "A valid PNG has nothing to report.");
            Assert.That(song.HasCoverArt, Is.True);
            Assert.That(song.CoverArtFileName, Is.EqualTo("sleeve.png"));
            Assert.That(song.CoverArtFileSize, Is.GreaterThan(0), "The row shows the size beside the name.");
        });
    }

    [Test]
    public async Task OnTheServerPath_TheImageIsLeftWhereTheUploadLooksForIt()
    {
        // The two paths resolve artwork from different places, and a row registered in the wrong one
        // publishes with no artwork and no error anywhere - the failure this asserts against.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);

        await page.Browse(song, FakeImage("sleeve.png"));

        Assert.Multiple(() =>
        {
            Assert.That(page.TempPathFor(song), Is.Not.Null, "The server path uploads from a temp file.");
            Assert.That(File.Exists(page.TempPathFor(song)), Is.True, "And that file has to still be there.");
            Assert.That(page.StagedPathFor(song), Is.Null, "Nothing was staged, so nothing may claim to be.");
            Assert.That(_staged, Is.Empty,
                "A batch the server is uploading has no staging folder to put this in.");
        });
    }

    [Test]
    public async Task OnTheDirectPath_TheImageIsStagedBecauseTheBrowserCannotSendIt()
    {
        // The browser uploads by FileList position, and a file browsed for here has none - it came
        // through a different input. So the server finishes the journey, or the row would point at a
        // blob that never existed.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: true);

        await page.Browse(song, FakeImage("sleeve.png"));

        Assert.Multiple(() =>
        {
            Assert.That(page.StagedPathFor(song), Is.EqualTo(StagedBlobPath));
            Assert.That(page.TempPathFor(song), Is.Null, "The staged blob is the copy that gets used.");
        });
    }

    [Test]
    public async Task StagedArtIsPutUnderTheBatchSweep_SoAbandoningTheBatchDoesNotOrphanIt()
    {
        // Without a batch id recorded, nothing on this page knows the blob exists and only the
        // container's seven-day lifecycle rule would ever remove it.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: true);

        await page.Browse(song, FakeImage("sleeve.png"));

        Assert.That(page.PendingImageBatch, Is.Not.Null.And.Not.EqualTo(Guid.Empty));
    }

    // -----------------------------------------------------------------
    // Correcting a pairing the matcher got wrong.
    // -----------------------------------------------------------------

    [Test]
    public async Task ReplacingArtOnASongThatHasSome_ReturnsTheOldImageToThePool()
    {
        // Same rule as dragging one image off a song: the creator is very often about to put the
        // displaced image on a different song, and stranding it would make them do that by hand.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false, coverArt: "wrong.jpg");

        await page.Browse(song, FakeImage("right.png"));

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtFileName, Is.EqualTo("right.png"));
            Assert.That(page.Pool, Is.EqualTo(new[] { "wrong.jpg" }), "The displaced image stays reachable.");
        });
    }

    [Test]
    public async Task ABrowsedImageIsNotAlsoLeftInThePool()
    {
        // The other half of the invariant. An image both assigned and pooled can be dropped onto a
        // second song, and two songs would publish claiming the same artwork.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);

        await page.Browse(song, FakeImage("sleeve.png"));

        Assert.That(page.Pool, Does.Not.Contain("sleeve.png"));
    }

    [Test]
    public async Task AnImageBrowsedForTwiceOnTheSameRow_LeavesOnlyTheSecondAssigned()
    {
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);

        await page.Browse(song, FakeImage("first.png"));
        await page.Browse(song, FakeImage("second.png"));

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtFileName, Is.EqualTo("second.png"));
            Assert.That(page.Pool, Is.EqualTo(new[] { "first.png" }));
        });
    }

    // -----------------------------------------------------------------
    // Names, which every store here is keyed by.
    // -----------------------------------------------------------------

    [Test]
    public async Task TwoBrowsedImagesSharingAName_AreKeptApart()
    {
        // A creator browsing from two folders has no idea what the batch already holds. Sharing a
        // key would mean sharing a staged blob, a size and a thumbnail - so the second song would
        // silently show and publish the first song's picture.
        var page = NewPage();
        var first = page.GivenSong("All Around Me", onDirectPath: false);
        var second = page.GivenSong("Get It Back", onDirectPath: false);

        await page.Browse(first, FakeImage("cover.png"));
        await page.Browse(second, FakeImage("cover.png"));

        Assert.Multiple(() =>
        {
            Assert.That(first.CoverArtFileName, Is.EqualTo("cover.png"));
            Assert.That(second.CoverArtFileName, Is.EqualTo("cover (2).png"),
                "The extension decides the blob name and content type, so only the stem may change.");
            Assert.That(page.TempPathFor(second), Is.Not.EqualTo(page.TempPathFor(first)),
                "Different names must mean different files, or the rename achieved nothing.");
        });
    }

    [Test]
    public async Task ABrowsedImageDoesNotStealTheNameOfOneAlreadyInThePool()
    {
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);
        page.GivenPooled("cover.png");

        await page.Browse(song, FakeImage("cover.png"));

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtFileName, Is.EqualTo("cover (2).png"));
            Assert.That(page.Pool, Is.EqualTo(new[] { "cover.png" }), "The pooled image is untouched.");
        });
    }

    [Test]
    public async Task EachStagedImageGetsItsOwnSlot()
    {
        // MatchBatchImage names blobs by index. Reusing one would overwrite a different song's
        // artwork with this one.
        var page = NewPage();
        var first = page.GivenSong("All Around Me", onDirectPath: true);
        var second = page.GivenSong("Get It Back", onDirectPath: true);

        await page.Browse(first, FakeImage("a.png"));
        await page.Browse(second, FakeImage("b.png"));

        Assert.Multiple(() =>
        {
            Assert.That(_staged.Select(call => call.Index), Is.Unique);
            Assert.That(_staged.Select(call => call.BatchId).Distinct().Count(), Is.EqualTo(1),
                "One batch folder, so one sweep removes both if the creator changes their mind.");
            Assert.That(_staged.Select(call => call.Extension), Is.All.EqualTo(".png"),
                "The extension decides the blob's name and content type downstream.");
        });
    }

    // -----------------------------------------------------------------
    // Rejections. The accept attribute is a filter, not a guarantee.
    // -----------------------------------------------------------------

    [Test]
    public async Task AFileThatIsNotAnImageType_IsRefused()
    {
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);

        await page.Browse(song, FakeImage("notes.pdf"));

        Assert.Multiple(() =>
        {
            Assert.That(song.HasCoverArt, Is.False);
            Assert.That(song.CoverArtError, Does.Contain(".png"),
                "The message has to say what is allowed, not just that this was not.");
        });
    }

    [Test]
    public async Task AFileNamedLikeAnImageButNotOne_IsRefused()
    {
        // The extension is the creator's claim; the decode is the check. Staging something nothing
        // downstream can read would fail the artwork minutes later, in a log they never see.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);

        await page.Browse(song, FakeFile("sleeve.png", "this is not a picture"u8.ToArray()));

        Assert.Multiple(() =>
        {
            Assert.That(song.HasCoverArt, Is.False);
            Assert.That(song.CoverArtError, Is.Not.Null);
        });
    }

    [Test]
    public async Task AnOversizedImage_IsRefusedByItsDeclaredSizeBeforeAnyStreamIsOpened()
    {
        // Reading first and objecting afterwards is what OpenReadStream's own cap does, and its
        // IOException escaping an event handler takes the circuit down with the whole batch.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);

        await page.Browse(song, FakeFile("huge.png", [1, 2, 3], declaredSize: 64L * 1024 * 1024));

        Assert.Multiple(() =>
        {
            Assert.That(song.HasCoverArt, Is.False);
            Assert.That(song.CoverArtError, Does.Contain("MB"));
        });
    }

    [Test]
    public async Task ARejectedImage_LeavesTheArtTheRowAlreadyHad()
    {
        // A mis-click must not cost a good pairing. Nothing is written to the row until every check
        // has passed, which is why this holds for every rejection above.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false, coverArt: "good.jpg");

        await page.Browse(song, FakeImage("notes.pdf"));

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtFileName, Is.EqualTo("good.jpg"));
            Assert.That(song.HasCoverArt, Is.True);
            Assert.That(page.Pool, Is.Empty, "Nothing was displaced, so nothing may be pooled.");
        });
    }

    [Test]
    public async Task AnErrorIsClearedByTheNextImageThatWorks()
    {
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);

        await page.Browse(song, FakeImage("notes.pdf"));
        await page.Browse(song, FakeImage("sleeve.png"));

        Assert.That(song.CoverArtError, Is.Null);
    }

    [Test]
    public async Task ARowThatIsNoLongerInTheBatch_SaysSoRatherThanSilentlyDoingNothing()
    {
        // Reachable by dropping a song from the batch while its file dialog is open.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);
        page.DropFromBatch(song);

        await page.Browse(song, FakeImage("sleeve.png"));

        Assert.Multiple(() =>
        {
            Assert.That(song.HasCoverArt, Is.False);
            Assert.That(song.CoverArtError, Is.Not.Null);
        });
    }

    [Test]
    public async Task NothingIsAcceptedOnceTheBatchHasLeftTheReviewStep()
    {
        // Past this point the row's job has been queued with whatever artwork it had, so accepting an
        // image would show the creator art that is never going to reach their song.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);
        page.EndReview();

        await page.Browse(song, FakeImage("sleeve.png"));

        Assert.That(song.HasCoverArt, Is.False);
    }

    // -----------------------------------------------------------------
    // The thumbnail, which is the only reason any of this is visible.
    // -----------------------------------------------------------------

    [Test]
    public async Task ABrowsedImageCarriesItsOwnThumbnail()
    {
        // The JS previewer addresses images by FileList position and a browsed one has none, so
        // without this the row shows a broken picture beside every matched one that works.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);

        await page.Browse(song, FakeImage("sleeve.png"));

        Assert.That(page.PreviewFor("sleeve.png"), Does.StartWith("data:image/webp;base64,"));
    }

    [Test]
    public async Task ThumbnailsAreDownscaled_NotTheOriginalInlinedAsBase64()
    {
        // A data URL of a 20 MB master would be some 27 MB of text in the markup, for a picture
        // rendered at 48 device-independent pixels.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false);
        var original = ImageBytes("sleeve.png", 1200);

        await page.Browse(song, FakeFile("sleeve.png", original));

        Assert.That(page.PreviewFor("sleeve.png").Length, Is.LessThan(original.Length),
            "A thumbnail larger than its source is not a thumbnail.");
    }

    [Test]
    public async Task AnImageTheBatchBroughtWithIt_HasNoInlineThumbnail()
    {
        // It is addressable in the browser's own FileList, so JavaScript points the element at it and
        // no bytes cross the circuit. A src here would pre-empt that, filling an element the previewer
        // expects to find empty.
        var page = NewPage();
        var song = page.GivenSong("Midnight Drive", onDirectPath: false, coverArt: "matched.png");

        await page.Browse(song, FakeImage("browsed.png"));

        Assert.Multiple(() =>
        {
            Assert.That(page.PreviewFor("browsed.png"), Is.Not.Null, "This one JavaScript cannot reach.");
            Assert.That(page.PreviewFor("matched.png"), Is.Null, "This one it can.");
        });
    }

    // -----------------------------------------------------------------
    // Test doubles.
    // -----------------------------------------------------------------

    private static IBrowserFile FakeImage(string fileName) => FakeFile(fileName, ImageBytes(fileName, 320));

    private static IBrowserFile FakeFile(string fileName, byte[] content, long? declaredSize = null)
        => new FakeBrowserFile(fileName, content, declaredSize ?? content.Length);

    /// <summary>
    /// A real encoded image, so the decode check and the thumbnail encoder are exercised rather than
    /// stubbed - the two steps most likely to be wrong about a file are the two that read it.
    /// </summary>
    private static byte[] ImageBytes(string fileName, int size)
    {
        using var bitmap = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
        }

        var format = Path.GetExtension(fileName).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? SKEncodedImageFormat.Png
            : SKEncodedImageFormat.Jpeg;

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private sealed class FakeBrowserFile(string name, byte[] content, long declaredSize) : IBrowserFile
    {
        public string Name { get; } = name;

        public DateTimeOffset LastModified { get; } = DateTimeOffset.UnixEpoch;

        /// <summary>
        /// What the browser claims, which is not always what it sends. Separate from the content so a
        /// test can pose the oversized case without allocating the bytes to go with it.
        /// </summary>
        public long Size { get; } = declaredSize;

        public string ContentType { get; } =
            MusicFileExtensions.GetCoverArtContentType(Path.GetExtension(name)) ?? "application/octet-stream";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
            => new MemoryStream(content);
    }

    /// <summary>
    /// Reaches the browse handler. Nothing here renders, so the component needs no DI graph beyond the
    /// staging service and a logger - the same approach <see cref="UploadFilesCoverArtRepairTests"/>
    /// takes to the re-pairing commands.
    /// </summary>
    private sealed class TestableUploadFiles : UploadFilesModel
    {
        public TestableUploadFiles(IUploadStagingSasService staging)
        {
            UploadStagingSasService = staging;
            LoggerFactory = NullLoggerFactory.Instance;
        }

        public IReadOnlyList<string> Pool => _unmatchedCoverArtFiles;

        public Guid? PendingImageBatch => _pendingImageBatchId;

        public UploadPairItem GivenSong(string title, bool onDirectPath, string coverArt = null)
        {
            var item = new UploadPairItem
            {
                SongTitle = title,
                AudioFileName = title + ".wav",
                CoverArtFileName = coverArt ?? string.Empty,
                HasCoverArt = coverArt is not null
            };

            _uploadItems.Add(item);

            // A direct-path row carries a FileList position instead of a temp file, and that is the
            // only thing distinguishing the two paths at this point.
            _pendingUploads.Add(new PendingUpload(
                item,
                onDirectPath ? null : Path.GetTempFileName(),
                onDirectPath ? _uploadItems.Count - 1 : null));

            _awaitingTitleConfirmation = true;
            return item;
        }

        public void GivenPooled(params string[] fileNames) => _unmatchedCoverArtFiles.AddRange(fileNames);

        public void DropFromBatch(UploadPairItem item)
            => _pendingUploads.RemoveAll(pending => ReferenceEquals(pending.Item, item));

        public void EndReview() => _awaitingTitleConfirmation = false;

        public Task Browse(UploadPairItem item, IBrowserFile file) => ApplyBrowsedCoverArtAsync(item, file);

        public string TempPathFor(UploadPairItem item) => ResolveCoverArtTempPath(item);

        public string StagedPathFor(UploadPairItem item) => ResolveCoverArtStagedPath(item);

        public string PreviewFor(string fileName) => CoverArtPreviewSource(fileName);

        /// <summary>Removes what a batch buffered, which nothing else in a test is going to do.</summary>
        public void DeleteTempFiles()
        {
            var paths = _uploadItems.Select(ResolveCoverArtTempPath)
                .Concat(_pendingUploads.Select(pending => pending.AudioTempPath));

            foreach (var path in paths.Where(path => path is not null && File.Exists(path)))
            {
                File.Delete(path);
            }
        }
    }
}
