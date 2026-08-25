using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Components;

/// <summary>
/// The review step is now the only place a song's genre can be set before it publishes, so what it
/// refuses to let through matters as much as what it collects.
/// </summary>
[TestFixture]
public class UploadFilesReviewMetadataTests
{
    // ------------------------------------------------------------------
    // A title is never empty, which is the whole problem.
    // ------------------------------------------------------------------

    [TestCase("03 shed sessions", TestName = "ALeadingTrackNumberLooksLikeAFileName")]
    [TestCase("track 03 final FINAL", TestName = "AVersionWordLooksLikeAFileName")]
    [TestCase("My Song Title 343543422", TestName = "ALongRunOfDigitsLooksLikeAFileName")]
    [TestCase("Untitled", TestName = "UntitledLooksLikeAFileName")]
    public void TitlesThatStillReadLikeFileNamesAreFlagged(string title)
    {
        Assert.That(UploadFilesModel.TitleLooksLikeAFileName(title), Is.True);
    }

    [TestCase("Long Way Down")]
    [TestCase("4 Minutes")]
    [TestCase("99 Red Balloons")]
    [TestCase("Take Me Home")]
    public void TitlesSomeoneActuallyChoseAreNotFlagged(string title)
    {
        // The rule has to stay narrow. Nagging people who name their files properly is worse than
        // missing the occasional bad one, because the confirmation catches those anyway.
        Assert.That(UploadFilesModel.TitleLooksLikeAFileName(title), Is.False);
    }

    [Test]
    public void AnEmptyTitleIsNotFlaggedAsAFileName()
    {
        // Empty is a validation error with its own message. Calling it filename-ish would replace a
        // precise complaint with a vague one.
        Assert.That(UploadFilesModel.TitleLooksLikeAFileName(""), Is.False);
        Assert.That(UploadFilesModel.TitleLooksLikeAFileName("   "), Is.False);
    }

    // ------------------------------------------------------------------
    // What a row carries to the job.
    // ------------------------------------------------------------------

    [Test]
    public void ARowCarriesEverythingTheCreatorSet()
    {
        var row = new UploadFilesModel.UploadPairItem
        {
            SongTitle = "Long Way Down",
            Genre = "  Alt Rock  ",
            PersonaId = 7,
            IsAiGenerated = true,
            IsAiLyrics = true,
        };

        var metadata = row.ToPublishMetadata();

        Assert.Multiple(() =>
        {
            Assert.That(metadata.Genre, Is.EqualTo("Alt Rock"), "trimmed, because it reaches the song card as-is");
            Assert.That(metadata.PersonaId, Is.EqualTo(7));
            Assert.That(metadata.IsAiGenerated, Is.True);
            Assert.That(metadata.IsAiLyrics, Is.True);
            Assert.That(metadata.IsAiVocals, Is.False);
        });
    }

    [Test]
    public void ARowWithNoGenreCarriesNullRatherThanEmpty()
    {
        // The job column is nullable and the difference is meaningful downstream: empty string is a
        // genre nobody chose, null is the absence of one.
        var row = new UploadFilesModel.UploadPairItem { SongTitle = "Untitled", Genre = "   " };

        Assert.That(row.ToPublishMetadata().Genre, Is.Null);
    }

    [Test]
    public void ARowWithNoPersonaCarriesNull_WhichIsANormalCase()
    {
        // Most creators have no persona at all. Their songs carry the display name, and that is a
        // choice the review step shows rather than an empty value it tolerates.
        var row = new UploadFilesModel.UploadPairItem { SongTitle = "Long Way Down", Genre = "Folk" };

        Assert.Multiple(() =>
        {
            Assert.That(row.ToPublishMetadata().PersonaId, Is.Null);
            Assert.That(row.ToPublishMetadata().Genre, Is.EqualTo("Folk"));
        });
    }

    [Test]
    public void ANewRowIsUnconfirmed()
    {
        // The default has to be false or the confirmation means nothing: every title arrives
        // pre-filled from its filename and would otherwise sail through unlooked-at.
        Assert.That(new UploadFilesModel.UploadPairItem().TitleConfirmed, Is.False);
    }

    // ------------------------------------------------------------------
    // The step can no longer be skipped.
    // ------------------------------------------------------------------

    [Test]
    public void EveryBatchPausesForReview()
    {
        // An audio-only batch, or one whose owner turned the cover-art checkbox off, used to go
        // straight up. That stopped being safe when genre moved onto this page: a song cannot
        // publish without one, and this is the only place to set it. The checkbox itself is gone.
        Assert.That(UploadFilesModel.ShouldPauseForReview(), Is.True);
    }

    // ------------------------------------------------------------------
    // The AI disclosure is a required choice between two groups.
    // ------------------------------------------------------------------

    [Test]
    public void ANewRowHasNotDeclaredAnything()
    {
        // The state that used to be impossible to see. Three unticked boxes read as "no AI", so
        // a creator who never looked at the column published the same disclosure as one who did.
        var row = new UploadFilesModel.UploadPairItem();

        Assert.Multiple(() =>
        {
            Assert.That(row.AiDeclared, Is.False);
            Assert.That(row.AllOriginal, Is.False);
            Assert.That(row.IsAiGenerated, Is.False);
        });
    }

    [Test]
    public void SayingAllOriginalClearsEveryAiAnswer()
    {
        var page = new UploadFilesModel();
        var row = new UploadFilesModel.UploadPairItem
        {
            IsAiGenerated = true,
            IsAiVocals = true,
            IsAiLyrics = true,
        };

        page.SetAllOriginal(row, true);

        Assert.Multiple(() =>
        {
            Assert.That(row.AllOriginal, Is.True);
            Assert.That(row.IsAiGenerated, Is.False);
            Assert.That(row.IsAiVocals, Is.False);
            Assert.That(row.IsAiLyrics, Is.False);
            Assert.That(row.AiDeclared, Is.True);
        });
    }

    [Test]
    public void NamingAnyAiPartClearsAllOriginal()
    {
        // The two cannot both be true: "none of this is AI, and the vocals are AI" is not an
        // answer, and letting it be selectable would put it in the database.
        var page = new UploadFilesModel();
        var row = new UploadFilesModel.UploadPairItem { AllOriginal = true };

        page.SetAiVocals(row, true);

        Assert.Multiple(() =>
        {
            Assert.That(row.AllOriginal, Is.False);
            Assert.That(row.IsAiVocals, Is.True);
            Assert.That(row.AiDeclared, Is.True);
        });
    }

    [Test]
    public void UntickingTheOnlyAiAnswerLeavesTheRowUndeclaredAgain()
    {
        // Not silently back to "all original". Changing your mind means answering again, which is
        // the whole point of the choice being required.
        var page = new UploadFilesModel();
        var row = new UploadFilesModel.UploadPairItem();

        page.SetAiMusic(row, true);
        page.SetAiMusic(row, false);

        Assert.Multiple(() =>
        {
            Assert.That(row.AiDeclared, Is.False);
            Assert.That(row.AllOriginal, Is.False);
        });
    }

    [TestCase(true, false, false, false)]
    [TestCase(false, true, false, false)]
    [TestCase(false, false, true, false)]
    [TestCase(false, false, false, true)]
    public void AnyOneAnswerCountsAsDeclared(bool music, bool vocals, bool lyrics, bool allOriginal)
    {
        var row = new UploadFilesModel.UploadPairItem
        {
            IsAiGenerated = music,
            IsAiVocals = vocals,
            IsAiLyrics = lyrics,
            AllOriginal = allOriginal,
        };

        Assert.That(row.AiDeclared, Is.True);
    }

    [Test]
    public void AllOriginalSendsNoAiFlagsToTheSong()
    {
        // What reaches the song is the three flags. "All original" is how the review step tells a
        // declaration apart from silence, and it has no column of its own because it does not
        // need one.
        var page = new UploadFilesModel();
        var row = new UploadFilesModel.UploadPairItem { SongTitle = "Long Way Down", Genre = "Folk" };
        page.SetAllOriginal(row, true);

        var metadata = row.ToPublishMetadata();

        Assert.Multiple(() =>
        {
            Assert.That(metadata.IsAiGenerated, Is.False);
            Assert.That(metadata.IsAiVocals, Is.False);
            Assert.That(metadata.IsAiLyrics, Is.False);
        });
    }

    [Test]
    public void AnUndeclaredRowBlocksTheUpload()
    {
        var gate = Slice(ReadCodeBehind(), "private async Task<bool> PendingBatchNeedsAttentionAsync");

        Assert.Multiple(() =>
        {
            Assert.That(gate, Does.Contain("AiDeclared"));
            Assert.That(gate, Does.Contain("AiError"));
        });
    }

    // ------------------------------------------------------------------
    // The same disclosure, in bulk.
    // ------------------------------------------------------------------

    [Test]
    public void BulkAllOriginalAnswersEverySong()
    {
        var page = WithRows(3);

        page.ApplyAllOriginal(true);

        Assert.That(Rows(page).All(r => r.AllOriginal && r.AiDeclared), Is.True);
    }

    [Test]
    public void BulkAiAnswersEverySong()
    {
        // A batch is usually one release, so a whole EP sharing an AI answer is normal rather
        // than exceptional.
        var page = WithRows(3);

        page.ApplyAllAiVocals(true);

        Assert.Multiple(() =>
        {
            Assert.That(Rows(page).All(r => r.IsAiVocals), Is.True);
            Assert.That(Rows(page).All(r => r.AiDeclared), Is.True);
            Assert.That(Rows(page).Any(r => r.IsAiGenerated), Is.False, "only the answer given");
        });
    }

    [Test]
    public void BulkKeepsTheTwoGroupsExclusive()
    {
        // The same rule a row follows. Without it the bulk control could put a combination on
        // every song that the row control refuses to let anyone pick.
        var page = WithRows(2);

        page.ApplyAllAiMusic(true);
        page.ApplyAllOriginal(true);

        Assert.Multiple(() =>
        {
            Assert.That(Rows(page).All(r => r.AllOriginal), Is.True);
            Assert.That(Rows(page).Any(r => r.IsAiGenerated), Is.False);
        });

        page.ApplyAllAiLyrics(true);

        Assert.Multiple(() =>
        {
            Assert.That(Rows(page).Any(r => r.AllOriginal), Is.False);
            Assert.That(Rows(page).All(r => r.IsAiLyrics), Is.True);
        });
    }

    [Test]
    public void BulkOverwritesRowsThatWereAlreadyAnswered()
    {
        // Deliberate, and the same as genre and persona: a control that behaves differently
        // depending on state you cannot see is worse than one that plainly overwrites.
        var page = WithRows(2);
        var rows = Rows(page);
        page.SetAiMusic(rows[0], true);

        page.ApplyAllOriginal(true);

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].IsAiGenerated, Is.False);
            Assert.That(rows[0].AllOriginal, Is.True);
        });
    }

    [Test]
    public void UntickingTheLastBulkAnswerLeavesEverySongUndeclared()
    {
        // Not silently original, which is what the whole change exists to prevent.
        var page = WithRows(2);

        page.ApplyAllAiVocals(true);
        page.ApplyAllAiVocals(false);

        Assert.That(Rows(page).Any(r => r.AiDeclared), Is.False);
    }

    private static UploadFilesModel WithRows(int count)
    {
        var page = new UploadFilesModel();
        var rows = Rows(page);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new UploadFilesModel.UploadPairItem { SongTitle = $"Song {i}" });
        }

        return page;
    }

    private static List<UploadFilesModel.UploadPairItem> Rows(UploadFilesModel page)
    {
        var field = typeof(UploadFilesModel).GetField(
            "_uploadItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "Expected _uploadItems to exist.");

        return (List<UploadFilesModel.UploadPairItem>)field!.GetValue(page)!;
    }

    // ------------------------------------------------------------------
    // Dropping one song without cancelling the batch.
    // ------------------------------------------------------------------

    [Test]
    public void TheRowActionSaysWhatItRemoves()
    {
        // Two buttons on the same row both saying "Remove" is how a creator trying to drop a song
        // from the batch ended up deleting its artwork instead.
        var markup = ReadRazor();

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("Content=\"Remove art\""),
                "the cover-art button has to name what it takes away");
            Assert.That(markup, Does.Contain("RemoveFromBatchAsync(item)"),
                "and there has to be a way to drop the song itself");
            Assert.That(markup, Does.Not.Contain("Content=\"Remove\""),
                "a bare \"Remove\" beside another Remove is the ambiguity being fixed");
        });
    }

    [Test]
    public void RemovingASongReturnsItsArtworkToThePool()
    {
        // The image may well belong to one of the songs that is staying, and this is exactly the
        // moment a creator finds that out. Discarding it would make them re-pick the file.
        var codeBehind = ReadCodeBehind();
        var method = Slice(codeBehind, "protected async Task RemoveFromBatchAsync");

        Assert.That(method, Does.Contain("ClearCoverArt(item)"));
    }

    [Test]
    public void RemovingASongDeletesItsBufferedAudio()
    {
        // The batch may run for several more minutes after this, and nothing else comes back for
        // a file no row points at any more.
        var method = Slice(ReadCodeBehind(), "protected async Task RemoveFromBatchAsync");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("_pendingUploads.Remove(pending)"));
            Assert.That(method, Does.Contain("TempFileHelper.TryDelete(pending.AudioTempPath"));
            Assert.That(method, Does.Contain("_pendingTempFiles.Remove(pending.AudioTempPath)"));
        });
    }

    [Test]
    public void RemovingTheLastSongTearsTheBatchDown()
    {
        // Otherwise the page sits on an empty review step behind an upload button that does
        // nothing, with no obvious way back to the drop box.
        var method = Slice(ReadCodeBehind(), "protected async Task RemoveFromBatchAsync");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("_uploadItems.Count == 0"));
            Assert.That(method, Does.Contain("await CancelPendingBatchAsync()"));
        });
    }

    private static string ReadRazor() =>
        ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor");

    private static string ReadCodeBehind() =>
        ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

    /// <summary>The body of one method, so an assertion cannot pass on a line somewhere else.</summary>
    private static string Slice(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Expected to find {signature}.");

        var next = source.IndexOf(N + "    protected ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static readonly string N = Environment.NewLine;

    private static string ReadProjectFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(GetRepositoryRoot(), Path.Combine(pathParts)));

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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [Test]
    public void PublishMetadataNone_IsWhatEveryOtherCallerSends()
    {
        var none = SongPublishMetadata.None;

        Assert.Multiple(() =>
        {
            Assert.That(none.Genre, Is.Null);
            Assert.That(none.PersonaId, Is.Null);
            Assert.That(none.IsAiGenerated, Is.False);
        });
    }
}
