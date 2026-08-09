using MusicSalesApp.Components.Pages.Creator;

namespace MusicSalesApp.Tests.Components;

/// <summary>
/// Re-pairing cover art at the review step.
///
/// <para>
/// Matching is a guess - OCR against a filename - and it fails two ways: it leaves an image unpaired,
/// and it pairs the wrong one. Both used to be permanent by the time the creator saw them.
/// </para>
///
/// <para>
/// One invariant governs every operation here: <b>each image is either assigned to exactly one song
/// or sitting in the pool - never both, never neither</b>. Break it and a creator either loses an
/// image they selected or publishes two songs claiming the same artwork, and neither shows up until
/// the batch is already in the catalogue.
/// </para>
/// </summary>
public class UploadFilesCoverArtRepairTests
{
    // -----------------------------------------------------------------
    // Filling a blank - the case that prompted the feature.
    // -----------------------------------------------------------------

    [Test]
    public void APooledImageDroppedOnABareSong_BecomesItsCoverArt()
    {
        var page = new TestableUploadFiles();
        var song = page.GivenSong("Midnight Drive");
        page.GivenPooled("stray.png");

        page.Hold("stray.png");
        page.Assign(song);

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtFileName, Is.EqualTo("stray.png"));
            Assert.That(song.HasCoverArt, Is.True);
            Assert.That(page.Pool, Is.Empty, "It cannot be in both places.");
            Assert.That(page.Held, Is.Null, "Putting it down completes the gesture.");
        });
    }

    [Test]
    public void TheImageIsNoLongerOfferedAsUnmatchedOnceItIsUsed()
    {
        var page = new TestableUploadFiles();
        var song = page.GivenSong("Midnight Drive");
        page.GivenPooled("a.png", "b.png");

        page.Hold("a.png");
        page.Assign(song);

        Assert.That(page.Pool, Is.EqualTo(new[] { "b.png" }));
    }

    // -----------------------------------------------------------------
    // Correcting a wrong pairing.
    // -----------------------------------------------------------------

    [Test]
    public void DraggingOneSongsArtOntoAnotherThatHasArt_SwapsThem()
    {
        var page = new TestableUploadFiles();
        // The backwards-pair fix, and why this is a swap rather than a displacement: a creator
        // dragging A's cover onto B is almost always correcting exactly this, and pushing B's cover
        // into the pool would leave them to do the second half by hand.
        var first = page.GivenSong("All Around Me", "all-around.jpg");
        var second = page.GivenSong("Get It Back", "getitback.png");

        page.Hold("all-around.jpg");
        page.Assign(second);

        Assert.Multiple(() =>
        {
            Assert.That(second.CoverArtFileName, Is.EqualTo("all-around.jpg"));
            Assert.That(first.CoverArtFileName, Is.EqualTo("getitback.png"));
            Assert.That(page.Pool, Is.Empty, "A swap consumes both, so neither is orphaned.");
        });
    }

    [Test]
    public void DraggingASongsArtOntoABareSong_LeavesTheFirstBare()
    {
        var page = new TestableUploadFiles();
        // Not a swap: there is nothing to swap back. The source must end up genuinely empty rather
        // than holding an empty string that later reads as "has cover art".
        var donor = page.GivenSong("All Around Me", "all-around.jpg");
        var bare = page.GivenSong("Midnight Drive");

        page.Hold("all-around.jpg");
        page.Assign(bare);

        Assert.Multiple(() =>
        {
            Assert.That(bare.CoverArtFileName, Is.EqualTo("all-around.jpg"));
            Assert.That(donor.HasCoverArt, Is.False);
            Assert.That(donor.CoverArtFileName, Is.Empty);
            Assert.That(donor.CoverArtFileSize, Is.Zero);
            Assert.That(page.Pool, Is.Empty);
        });
    }

    [Test]
    public void APooledImageDroppedOnASongThatHasArt_SendsTheOldOneBackToThePool()
    {
        var page = new TestableUploadFiles();
        var song = page.GivenSong("All Around Me", "wrong.jpg");
        page.GivenPooled("right.png");

        page.Hold("right.png");
        page.Assign(song);

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtFileName, Is.EqualTo("right.png"));
            Assert.That(page.Pool, Is.EqualTo(new[] { "wrong.jpg" }), "The displaced image is not discarded.");
        });
    }

    // -----------------------------------------------------------------
    // Taking artwork off.
    // -----------------------------------------------------------------

    [Test]
    public void RemovingASongsArt_ReturnsItToThePool()
    {
        var page = new TestableUploadFiles();
        var song = page.GivenSong("All Around Me", "cover.jpg");

        page.Clear(song);

        Assert.Multiple(() =>
        {
            Assert.That(song.HasCoverArt, Is.False);
            Assert.That(page.Pool, Is.EqualTo(new[] { "cover.jpg" }));
        });
    }

    [Test]
    public void DraggingASongsArtBackToThePool_TakesItOffThatSong()
    {
        var page = new TestableUploadFiles();
        // The same gesture as dragging it onto another song, which the row's remove button is not -
        // so a creator mid-drag has somewhere to let go.
        var song = page.GivenSong("All Around Me", "cover.jpg");

        page.Hold("cover.jpg");
        page.DropOnPool();

        Assert.Multiple(() =>
        {
            Assert.That(song.HasCoverArt, Is.False);
            Assert.That(page.Pool, Is.EqualTo(new[] { "cover.jpg" }));
            Assert.That(page.Held, Is.Null);
        });
    }

    // -----------------------------------------------------------------
    // Gestures that must do nothing.
    // -----------------------------------------------------------------

    [Test]
    public void DroppingAnImageOnTheSongThatAlreadyHasIt_ChangesNothing()
    {
        var page = new TestableUploadFiles();
        // Must not bounce through the pool on the way, which would briefly break the invariant and
        // leave the image listed as unmatched if anything rendered in between.
        var song = page.GivenSong("All Around Me", "cover.jpg");

        page.Hold("cover.jpg");
        page.Assign(song);

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtFileName, Is.EqualTo("cover.jpg"));
            Assert.That(page.Pool, Is.Empty);
        });
    }

    [Test]
    public void DroppingWithNothingHeld_ChangesNothing()
    {
        var page = new TestableUploadFiles();
        // A stray click on a row is not an edit.
        var song = page.GivenSong("All Around Me", "cover.jpg");

        page.Assign(song);

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtFileName, Is.EqualTo("cover.jpg"));
            Assert.That(page.Pool, Is.Empty);
        });
    }

    [Test]
    public void RemovingArtFromASongThatHasNone_ChangesNothing()
    {
        var page = new TestableUploadFiles();
        var song = page.GivenSong("Midnight Drive");

        page.Clear(song);

        Assert.That(page.Pool, Is.Empty, "An empty name must never reach the pool.");
    }

    [Test]
    public void HoldingTheSameImageTwice_PutsItDown()
    {
        var page = new TestableUploadFiles();
        // The only way to cancel on a touch screen, where there is no drag to abandon.
        page.GivenPooled("stray.png");

        page.Hold("stray.png");
        page.Hold("stray.png");

        Assert.That(page.Held, Is.Null);
    }

    [Test]
    public void HoldingADifferentImage_SwitchesRatherThanStacking()
    {
        var page = new TestableUploadFiles();
        page.GivenPooled("a.png", "b.png");

        page.Hold("a.png");
        page.Hold("b.png");

        Assert.That(page.Held, Is.EqualTo("b.png"));
    }

    // -----------------------------------------------------------------
    // The invariant, under a sequence rather than a single operation.
    // -----------------------------------------------------------------

    [Test]
    public void EveryImageStaysInExactlyOnePlaceAcrossAWholeEditingSession()
    {
        var page = new TestableUploadFiles();
        var one = page.GivenSong("One", "a.png");
        var two = page.GivenSong("Two", "b.png");
        var three = page.GivenSong("Three");
        page.GivenPooled("c.png", "d.png");

        page.Hold("c.png");
        page.Assign(three);      // pool -> bare song

        page.Hold("a.png");
        page.Assign(two);        // song -> song, swaps with b.png

        page.Clear(one);         // song -> pool  (one was given b.png by the swap)

        page.Hold("d.png");
        page.Assign(one);        // pool -> now-bare song

        page.Hold("c.png");
        page.DropOnPool();       // song -> pool

        var assigned = page.Songs.Where(s => s.HasCoverArt).Select(s => s.CoverArtFileName).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(
                assigned.Concat(page.Pool).OrderBy(name => name),
                Is.EqualTo(new[] { "a.png", "b.png", "c.png", "d.png" }),
                "Every image selected in the batch is still accounted for exactly once.");

            Assert.That(assigned, Is.Unique, "No two songs may claim the same image.");
            Assert.That(page.Pool, Is.Unique);
            Assert.That(assigned.Intersect(page.Pool), Is.Empty, "Nothing is assigned and pooled at once.");
        });
    }

    // -----------------------------------------------------------------
    // What the creator is told.
    // -----------------------------------------------------------------

    [Test]
    public void SongsWithNoArtworkAreCounted_NotLeftToBeInferred()
    {
        var page = new TestableUploadFiles();
        // The gap in the old page: it listed the unmatched *image* but never said which *song* was
        // bare, so a creator with 24 tracks had to work that out for themselves.
        page.GivenSong("One", "a.png");
        page.GivenSong("Two");
        page.GivenSong("Three");

        Assert.That(page.BareSongCount, Is.EqualTo(2));
    }

    [Test]
    public void ThePoolIsOfferedEvenWhenEverythingMatched()
    {
        var page = new TestableUploadFiles();
        // Otherwise a batch where the matcher paired everything - wrongly - offers no way to fix it.
        page.GivenSong("One", "a.png");
        page.BeginReview();

        Assert.That(page.CanRepair, Is.True);
    }

    [Test]
    public void AnEmptyPoolIsNotRendered()
    {
        // An empty dashed box above a table where every pairing is already right announces a problem
        // that does not exist. Re-pairing is still fully available - the rows are the drag sources.
        var page = new TestableUploadFiles();
        page.GivenSong("One", "a.png");
        page.BeginReview();

        Assert.Multiple(() =>
        {
            Assert.That(page.CanRepair, Is.True, "The rows stay interactive.");
            Assert.That(page.ShowPool, Is.False, "But there is nothing to show in the pool.");
        });
    }

    [Test]
    public void PickingUpASongsArtBringsThePoolBack_SoThereIsSomewhereToDropIt()
    {
        // The other half of hiding it: without this, artwork on a fully-matched batch could be picked
        // up and then have nowhere to go.
        var page = new TestableUploadFiles();
        page.GivenSong("One", "a.png");
        page.BeginReview();

        page.Hold("a.png");

        Assert.That(page.ShowPool, Is.True);
    }

    [Test]
    public void ThePoolIsRenderedWheneverItHoldsSomething()
    {
        var page = new TestableUploadFiles();
        page.GivenSong("One");
        page.GivenPooled("stray.png");

        Assert.That(page.ShowPool, Is.True);
    }

    [Test]
    public void ThereIsNothingToRepairInABatchWithNoArtworkAtAll()
    {
        var page = new TestableUploadFiles();
        page.GivenSong("One");
        page.BeginReview();

        Assert.That(page.CanRepair, Is.False);
    }

    [Test]
    public void NothingIsRepairableOnceUploadingHasStarted()
    {
        var page = new TestableUploadFiles();
        // The rows keep rendering through processing, and an assignment made then would change a
        // label without changing anything that has already been staged.
        page.GivenSong("One", "a.png");
        page.EndReview();

        Assert.That(page.CanRepair, Is.False, "Review is over; the cell is a read-only label again.");
    }

    // -----------------------------------------------------------------
    // When the batch stops for review, and when it does not.
    // -----------------------------------------------------------------

    [Test]
    public void ABatchWithNoArtworkAtAll_DoesNotPause()
    {
        // Nothing to pair, no pool to drag from - the step would be a confirmation dialog wearing a
        // table, and audio-only batches are a normal way to use this page.
        Assert.That(
            UploadFilesModel.ShouldPauseForReview(
                titlesNeedAttention: false, batchHasCoverArt: false, matchCoverArtBeforeUpload: true),
            Is.False);
    }

    [Test]
    public void ABatchWithArtwork_Pauses()
    {
        Assert.That(
            UploadFilesModel.ShouldPauseForReview(
                titlesNeedAttention: false, batchHasCoverArt: true, matchCoverArtBeforeUpload: true),
            Is.True);
    }

    [Test]
    public void TurningTheCheckboxOff_SkipsThePauseEvenWithArtwork()
    {
        Assert.That(
            UploadFilesModel.ShouldPauseForReview(
                titlesNeedAttention: false, batchHasCoverArt: true, matchCoverArtBeforeUpload: false),
            Is.False);
    }

    [Test]
    public void ABrokenTitleStopsTheBatchWhateverElseIsTurnedOff()
    {
        // Not a preference. The upload would be rejected by the server, so skipping the step would
        // trade one pause for a failed batch.
        Assert.Multiple(() =>
        {
            Assert.That(
                UploadFilesModel.ShouldPauseForReview(
                    titlesNeedAttention: true, batchHasCoverArt: false, matchCoverArtBeforeUpload: false),
                Is.True);

            Assert.That(
                UploadFilesModel.ShouldPauseForReview(
                    titlesNeedAttention: true, batchHasCoverArt: true, matchCoverArtBeforeUpload: false),
                Is.True);
        });
    }

    [Test]
    public void TheRepairInterfaceIsHiddenWhenTheCheckboxIsOff()
    {
        // "Do not show the interface at all" has to hold on the one path that still reaches the
        // review step with the box unticked: a batch stopped for a broken title.
        var page = new TestableUploadFiles();
        page.GivenSong("One", "a.png");
        page.GivenPooled("stray.png");
        page.BeginReview();

        Assert.That(page.CanRepair, Is.True);

        page.DisableCoverArtMatching();

        Assert.That(page.CanRepair, Is.False);
    }

    [Test]
    public void TurningTheCheckboxOffMidGesture_PutsDownWhateverWasHeld()
    {
        // Otherwise the held image survives with nothing on screen to place it on, and the next
        // batch starts holding a file from the last one.
        var page = new TestableUploadFiles();
        page.GivenPooled("stray.png");
        page.Hold("stray.png");

        page.DisableCoverArtMatching();

        Assert.That(page.Held, Is.Null);
    }

    // -----------------------------------------------------------------
    // Thumbnails.
    // -----------------------------------------------------------------

    [Test]
    public void AnImagesThumbnailIdFollowsTheImage_NotItsPosition()
    {
        var page = new TestableUploadFiles();
        // Ids are handed to JS, which points each element at the browser's own copy of that file. A
        // positional id would address a different picture after every re-pairing and the thumbnails
        // would silently swap.
        var id = UploadFilesModel.PreviewElementId("cover.jpg");

        Assert.Multiple(() =>
        {
            Assert.That(UploadFilesModel.PreviewElementId("cover.jpg"), Is.EqualTo(id));
            Assert.That(UploadFilesModel.PreviewElementId("other.jpg"), Is.Not.EqualTo(id));
            Assert.That(id, Does.StartWith("cover-preview-"));
        });
    }

    [Test]
    public void AThumbnailIdIsAValidDomIdForAnyFileName()
    {
        var page = new TestableUploadFiles();
        // Filenames are unconstrained - spaces, quotes, non-Latin scripts all reach here - and any of
        // them landing in an id attribute raw would break getElementById or the markup around it.
        var id = UploadFilesModel.PreviewElementId("my cover \"art\" (2)_final ✓.png");

        Assert.That(id, Does.Match("^cover-preview-[0-9A-F]+$"));
    }

    /// <summary>
    /// Reaches the re-pairing commands. Nothing here renders or touches an injected service, so the
    /// component needs no DI graph - the same approach the visibility tests use.
    /// </summary>
    private sealed class TestableUploadFiles : UploadFilesModel
    {
        public IReadOnlyList<UploadPairItem> Songs => _uploadItems;
        public IReadOnlyList<string> Pool => _unmatchedCoverArtFiles;
        public string Held => HeldCoverArt;
        public bool CanRepair => CanRepairCoverArt;
        public bool ShowPool => ShowCoverArtPool;
        public int BareSongCount => SongsWithoutCoverArtCount;

        public UploadPairItem GivenSong(string title, string coverArt = null)
        {
            var item = new UploadPairItem
            {
                SongTitle = title,
                AudioFileName = title + ".wav",
                CoverArtFileName = coverArt ?? string.Empty,
                HasCoverArt = coverArt is not null
            };

            _uploadItems.Add(item);
            BeginReview();
            return item;
        }

        public void GivenPooled(params string[] fileNames)
        {
            _unmatchedCoverArtFiles.AddRange(fileNames);
            BeginReview();
        }

        /// <summary>The review step is where re-pairing is offered, so tests start inside it.</summary>
        public void BeginReview() => _awaitingTitleConfirmation = true;

        public void EndReview() => _awaitingTitleConfirmation = false;

        public void Hold(string fileName) => HoldCoverArt(fileName);

        public void Assign(UploadPairItem item) => AssignHeldCoverArt(item);

        public void Clear(UploadPairItem item) => ClearCoverArt(item);

        public void DropOnPool() => ReturnHeldCoverArtToPool();

        public void DisableCoverArtMatching() => ApplyMatchCoverArtPreference(false);
    }
}
