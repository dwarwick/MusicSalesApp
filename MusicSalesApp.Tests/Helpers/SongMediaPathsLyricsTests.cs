using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

/// <summary>
/// Where a song's lyrics artifacts go under each of the two naming schemes.
///
/// <para>
/// Songs uploaded from July 2026 onward live in a folder named for a GUID the application minted.
/// Older ones are named after the creator's own filename - <c>Night Drive/Night Drive.mp3</c> - and
/// have no GUID at all. Lyric timing supports both, which makes this the piece most likely to break
/// quietly: the GUID path is what every developer will test by hand, and the legacy path is what
/// most of the existing catalogue actually uses.
/// </para>
/// </summary>
[TestFixture]
public class SongMediaPathsLyricsTests
{
    private static readonly Guid MediaGuid = Guid.Parse("0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f");

    [Test]
    public void AGuidSchemeSongPutsItsLyricsInItsOwnFolder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SongMediaPaths.ResolveLyricsTextTarget(42, MediaGuid, "0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f/0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f-music.mp3"),
                Is.EqualTo("0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f/0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f-lyrics.txt"));
            Assert.That(
                SongMediaPaths.ResolveLyricsTimingsTarget(42, MediaGuid, null),
                Is.EqualTo("0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f/0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f-lyrics.json"));
            Assert.That(
                SongMediaPaths.ResolveLyricsLrcTarget(42, MediaGuid, null),
                Is.EqualTo("0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f/0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f-lyrics.lrc"));
        });
    }

    [Test]
    public void AGuidSchemeSongIgnoresItsPlaybackPathEntirely()
    {
        // The GUID is authoritative. Deriving anything from the stored path would make the target
        // depend on history rather than identity.
        Assert.That(
            SongMediaPaths.ResolveLyricsTimingsTarget(42, MediaGuid, "somewhere/else/entirely.mp3"),
            Is.EqualTo(SongMediaPaths.ResolveLyricsTimingsTarget(42, MediaGuid, null)));
    }

    [Test]
    public void ALegacySongPutsItsLyricsBesideItsAudio()
    {
        Assert.That(
            SongMediaPaths.ResolveLyricsTimingsTarget(42, null, "Night Drive/Night Drive.mp3"),
            Is.EqualTo("Night Drive/42-lyrics.json"));
    }

    [Test]
    public void ALegacySongsLyricsPathContainsNothingTheCreatorTyped()
    {
        // The single most important property here. Legacy blob paths ARE creator filenames, which
        // are unconstrained - apostrophes, ampersands, non-ASCII, anything. SongMediaPaths exists
        // precisely so those never reach storage, so a new artifact added in 2026 must be named from
        // the song's id, not from the folder it happens to sit in.
        const string awkward = "Bob's Big Night / 100% \"Live\"/Bob's Big Night.mp3";

        var path = SongMediaPaths.ResolveLyricsTimingsTarget(1234, null, awkward);

        Assert.Multiple(() =>
        {
            Assert.That(path, Does.EndWith("/1234-lyrics.json"));
            Assert.That(
                path.Split('/')[^1],
                Is.EqualTo("1234-lyrics.json"),
                "The leaf name must be derived from the song id alone.");
        });
    }

    [Test]
    public void ALegacySongWithNoAudioPathStillResolvesSomewhere()
    {
        // Defensive: a song with no playback blob cannot be aligned anyway, but returning null here
        // would turn a clean rejection upstream into a null-reference somewhere further down.
        Assert.That(
            SongMediaPaths.ResolveLyricsTimingsTarget(7, null, null),
            Is.EqualTo("7-lyrics.json"));
    }

    [Test]
    public void TwoLegacySongsInOneFolderDoNotCollide()
    {
        // Legacy folders are per-album, so several songs can share one. Naming from the song id is
        // what keeps their timings apart; naming from the folder would have them overwrite each other.
        var first = SongMediaPaths.ResolveLyricsTimingsTarget(1, null, "Greatest Hits/Track One.mp3");
        var second = SongMediaPaths.ResolveLyricsTimingsTarget(2, null, "Greatest Hits/Track Two.mp3");

        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public void BackslashesInALegacyPathAreNormalised()
    {
        // Some legacy rows were written on Windows and carry backslashes. A blob path with one in it
        // is a different blob.
        Assert.That(
            SongMediaPaths.ResolveLyricsTimingsTarget(9, null, @"Night Drive\Night Drive.mp3"),
            Is.EqualTo("Night Drive/9-lyrics.json"));
    }

    [Test]
    public void TheStagingFolderForAnAttemptCannotBeMistakenForAnUploadJobFolder()
    {
        // SongUploadJobService.DeleteStagedBlobsAsync deletes by "{guid}/". A lyrics attempt has its
        // own GUID, so without the prefix the two namespaces would overlap and an upload cleaning up
        // after itself could delete a lyrics attempt's output. The same reason match batches carry a
        // "batch/" prefix.
        var attemptId = Guid.NewGuid();
        var folder = MediaProcessingStagingPaths.LyricsFolder(attemptId);

        Assert.Multiple(() =>
        {
            Assert.That(folder, Does.StartWith("lyrics/"));
            Assert.That(
                folder,
                Is.Not.EqualTo(MediaProcessingStagingPaths.Folder(attemptId)),
                "A lyrics folder must not be reachable by an upload job's delete-by-prefix.");
            Assert.That(
                MediaProcessingStagingPaths.LyricsTimings(attemptId),
                Does.StartWith(folder + "/"));
            Assert.That(
                MediaProcessingStagingPaths.LyricsLrc(attemptId),
                Does.StartWith(folder + "/"));
        });
    }
}
