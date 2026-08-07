using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

/// <summary>
/// Blob names inside the staging container. These cross a process boundary — the web app writes
/// them, the Function reads them — so a drift produces no compiler error, just a 404 in a log.
/// </summary>
[TestFixture]
public class MediaProcessingStagingPathsTests
{
    private static readonly Guid JobId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Test]
    public void AJobFolder_MatchesTheEventualMediaFolder()
    {
        // The staging folder and the media folder are deliberately the same string, so one GUID
        // names the job end to end rather than needing a second correlation key.
        Assert.That(
            MediaProcessingStagingPaths.Folder(JobId),
            Is.EqualTo("0f8fad5bd9cb469fa16570867728950e"));
    }

    [Test]
    public void AMatchBatchFolder_IsPrefixedSoItIsNeverMistakenForAJobFolder()
    {
        // DeleteStagedBlobsAsync deletes by "{guid}/" prefix. A batch folder sitting at the same
        // level could be swept by a job that has nothing to do with it.
        Assert.That(
            MediaProcessingStagingPaths.MatchBatchFolder(JobId),
            Is.EqualTo("batch/0f8fad5bd9cb469fa16570867728950e"));
    }

    [Test]
    public void AMatchBatchImage_IsNamedByItsIndexRatherThanItsFilename()
    {
        // Creator filenames are unconstrained - apostrophes, ampersands, non-ASCII - and must never
        // reach a blob path. The filename travels in the queue message as data instead.
        Assert.That(
            MediaProcessingStagingPaths.MatchBatchImage(JobId, 3, ".jpg"),
            Is.EqualTo("batch/0f8fad5bd9cb469fa16570867728950e/3.jpg"));
    }

    [TestCase("jpg", "batch/0f8fad5bd9cb469fa16570867728950e/0.jpg")]
    [TestCase(".JPG", "batch/0f8fad5bd9cb469fa16570867728950e/0.jpg")]
    [TestCase("  .Png  ", "batch/0f8fad5bd9cb469fa16570867728950e/0.png")]
    [TestCase("", "batch/0f8fad5bd9cb469fa16570867728950e/0")]
    [TestCase(null, "batch/0f8fad5bd9cb469fa16570867728950e/0")]
    public void AMatchBatchImage_NormalizesItsExtension(string extension, string expected)
        => Assert.That(
            MediaProcessingStagingPaths.MatchBatchImage(JobId, 0, extension),
            Is.EqualTo(expected));

    [Test]
    public void EveryBatchImage_SitsUnderItsOwnBatchFolder()
    {
        // What makes the post-match cleanup a single prefix delete.
        var folder = MediaProcessingStagingPaths.MatchBatchFolder(JobId);

        for (var index = 0; index < 5; index++)
        {
            Assert.That(
                MediaProcessingStagingPaths.MatchBatchImage(JobId, index, ".png"),
                Does.StartWith(folder + "/"));
        }
    }

    [Test]
    public void TwoBatches_DoNotShareAFolder()
        => Assert.That(
            MediaProcessingStagingPaths.MatchBatchFolder(Guid.NewGuid()),
            Is.Not.EqualTo(MediaProcessingStagingPaths.MatchBatchFolder(Guid.NewGuid())));
}
