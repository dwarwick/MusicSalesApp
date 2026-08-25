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
    public void EveryBatchPauses()
    {
        // It did not used to: an audio-only batch, or one whose owner had turned off the
        // cover-art checkbox, went straight up. Genre moving onto this page ended both - a song
        // cannot publish without one, and the review step is the only place to set it.
        Assert.That(UploadFilesModel.ShouldPauseForReview(), Is.True);
    }

    [Test]
    public void TheRepairInterfaceIsAvailableWheneverThereIsArtworkToMove()
    {
        // There used to be a checkbox that took this away. It survived the review step becoming
        // mandatory as a switch whose only remaining effect was to remove a capability - the
        // automatic pairing is a guess, and turning off the only way to correct it helps nobody.
        var page = new TestableUploadFiles();
        page.GivenSong("One", "a.png");
        page.GivenPooled("stray.png");

        Assert.Multiple(() =>
        {
            Assert.That(page.CanRepair, Is.True);
            Assert.That(page.ShowPool, Is.True);
        });
    }

    // -----------------------------------------------------------------
    // Ways the pool used to lose images.
    // -----------------------------------------------------------------

    [Test]
    public void DismissingAValidationErrorDuringReview_KeepsThePool()
    {
        // ClearValidationError empties the lists shown under the banner, and _unmatchedCoverArtFiles
        // used to be one of them - it was a post-hoc report before re-pairing existed. StartUploadAsync
        // calls it before re-checking titles, so a batch bounced back for a duplicate title arrived
        // at the review step with every unplaced image silently gone.
        var page = new TestableUploadFiles();
        page.GivenSong("One");
        page.GivenPooled("a.png", "b.png");

        page.DismissValidationError();

        Assert.That(page.Pool, Is.EqualTo(new[] { "a.png", "b.png" }));
    }

    [Test]
    public void DismissingAValidationErrorAfterReview_StillClearsTheNotice()
    {
        // Once the batch is past re-pairing these really are just leftovers, and the banner's close
        // button has to keep working.
        var page = new TestableUploadFiles();
        page.GivenPooled("a.png");
        page.EndReview();

        page.DismissValidationError();

        Assert.That(page.Pool, Is.Empty);
    }

    [Test]
    public void AnImageIsMatchedCaseInsensitivelyWhenLeavingThePool()
    {
        // Every dictionary a filename travels through here is OrdinalIgnoreCase, and so is the
        // membership test in ReturnToPool - but List<string>.Remove is ordinal and case-sensitive.
        // The mismatch left an image assigned to a song AND still listed as unmatched, so it could
        // be handed to a second song, which the pipeline then resolves to one silent winner.
        var page = new TestableUploadFiles();
        var song = page.GivenSong("One");
        page.GivenPooled("Cover.PNG");

        page.Hold("cover.png");
        page.Assign(song);

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtFileName, Is.EqualTo("cover.png"));
            Assert.That(page.Pool, Is.Empty, "It cannot be assigned and pooled at the same time.");
        });
    }

    [Test]
    public void AnImageIsNotPooledTwiceUnderADifferentCasing()
    {
        var page = new TestableUploadFiles();
        var song = page.GivenSong("One", "Cover.PNG");
        page.GivenPooled("cover.png");

        page.Clear(song);

        Assert.That(page.Pool, Has.Count.EqualTo(1));
    }

    [Test]
    public void TheRemoveControlIsLabelled()
    {
        // It shipped as an icon-only SfButton with empty Content and rendered as nothing a creator
        // could see or click. There is no icon-only button anywhere else in this application, and a
        // creator who had already struggled to find the drop target was not going to find a bare x.
        var markup = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(
                GetRepositoryRoot(), "MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor")),
            @"\s+", " ");

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Not.Contain("Content=\"\""), "An SfButton with no content has nothing to click.");
            // "Remove art", not "Remove": a second Remove appeared on the row for dropping the
            // song itself, and a creator pressed the wrong one and lost their artwork.
            Assert.That(markup, Does.Contain("Content=\"Remove art\""));
            Assert.That(
                markup,
                Does.Contain("Tap or Drop Cover Art Here"),
                "Every empty cell needs its own label, not just the instruction above the table.");
        });
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
    public void WhetherAThumbnailNeedsRefreshingIsDecidedAgainstTheDom_NotFromCSharpState()
    {
        // Two attempts at deciding this in C# were both wrong, and each broke thumbnails a different
        // way. Keyed on which images exist, a re-pairing looked like no change - so the refresh was
        // skipped while the move had already destroyed the img the src was set on. Keyed on where
        // they are, the very first render of a one-song batch was skipped instead.
        //
        // The fault was the premise: C# cannot see whether an <img> currently holds the right src.
        // Re-pairing replaces elements, and so does an unrelated re-render. So .NET now sends the
        // full set every render and the JS compares against the live DOM, minting a URL only when
        // one is actually missing - which is also self-healing if an element is ever replaced.
        var codeBehind = ReadCodeBehind();
        var markup = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(
                codeBehind,
                Does.Not.Contain("_coverArtPreviewSignature"),
                "No C#-side guess about what the DOM is holding.");

            Assert.That(
                markup,
                Does.Contain("if (element.src !== url)"),
                "The JS must compare against the element it is about to write to.");

            Assert.That(
                markup,
                Does.Contain("held[pair.elementId] || URL.createObjectURL(file)"),
                "A URL already minted for an image must be reused wherever that image moved to.");
        });
    }

    [Test]
    public void ThumbnailElementsAreKeyedByFileName()
    {
        // Their src is set from JS, which Blazor knows nothing about. Unkeyed, it reuses one image's
        // element for another as the lists change and carries the previous thumbnail across.
        var markup = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(
                GetRepositoryRoot(), "MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor")),
            @"\s+", " ");

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("<div @key=\"file\""), "The pool chips.");
            Assert.That(markup, Does.Contain("<img @key=\"item.CoverArtFileName\""), "The row thumbnails.");
        });
    }

    private static string ReadCodeBehind()
        => File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs"));

    [Test]
    public void AThumbnailIdIsAValidDomIdForAnyFileName()
    {
        var page = new TestableUploadFiles();
        // Filenames are unconstrained - spaces, quotes, non-Latin scripts all reach here - and any of
        // them landing in an id attribute raw would break getElementById or the markup around it.
        var id = UploadFilesModel.PreviewElementId("my cover \"art\" (2)_final ✓.png");

        Assert.That(id, Does.Match("^cover-preview-[0-9A-F]+$"));
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

        public void DismissValidationError() => ClearValidationError();
    }
}
