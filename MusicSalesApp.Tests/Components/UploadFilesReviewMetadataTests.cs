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

    [TestCase(false, false, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    public void EveryBatchPausesForReview(bool badTitles, bool hasArt, bool matchPreference)
    {
        // An audio-only batch, or one whose owner turned the cover-art checkbox off, used to go
        // straight up. That stopped being safe when genre moved onto this page: a song cannot
        // publish without one, and this is the only place to set it.
        Assert.That(
            UploadFilesModel.ShouldPauseForReview(badTitles, hasArt, matchPreference),
            Is.True);
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
