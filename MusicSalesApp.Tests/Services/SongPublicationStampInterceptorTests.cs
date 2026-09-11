#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Models;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// FirstPublishedAtUtc has to mean the moment a song became public, because the whole follow
/// feature reasons about it: "was this follower already following when it came out?"
/// </summary>
/// <remarks>
/// It used to be written only by the hourly release job, which left a gap of up to an hour. A
/// listener who found a brand-new song at 12:20 and followed because of it counted as having
/// followed BEFORE the 12:40 stamp, so the first thing that happened to them was a notification
/// about the song they had just followed for - the single most common way a follow is acquired.
/// </remarks>
[TestFixture]
public class SongPublicationStampInterceptorTests
{
    private ArtistFollowTestHarness _harness;

    [SetUp]
    public void SetUp() => _harness = new ArtistFollowTestHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private SongMetadata NewSong(bool enabled = true, string? mp3Path = "songs/ocean-road.mp3") =>
        new()
        {
            SongTitle = "Ocean Road",
            Mp3BlobPath = mp3Path,
            BlobPath = "songs/ocean-road.mp3",
            PersonaId = _harness.PersonaId,
            CreatorId = _harness.CreatorId,
            IsActive = true,
            IsEnabled = enabled,
            IsAlbumCover = false,
        };

    private async Task<DateTime?> StampOfAsync(int songId)
    {
        await using var context = _harness.NewContext();
        return (await context.SongMetadata.SingleAsync(song => song.Id == songId)).FirstPublishedAtUtc;
    }

    [Test]
    public async Task ASongThatArrivesAlreadyPublicIsStampedOnInsert()
    {
        await using var context = _harness.NewContextWithPublicationStamp();
        var song = NewSong();

        context.SongMetadata.Add(song);
        await context.SaveChangesAsync();

        Assert.That(await StampOfAsync(song.Id), Is.Not.Null);
    }

    [Test]
    public async Task ADraftIsNotStamped()
    {
        // Not enabled, so not publicly released, so not published - whatever else is true of it.
        await using var context = _harness.NewContextWithPublicationStamp();
        var song = NewSong(enabled: false);

        context.SongMetadata.Add(song);
        await context.SaveChangesAsync();

        Assert.That(await StampOfAsync(song.Id), Is.Null);
    }

    [Test]
    public async Task ASongStillTranscodingIsNotStamped()
    {
        // No Mp3BlobPath yet: the Functions app has not called back, so there is nothing to play.
        await using var context = _harness.NewContextWithPublicationStamp();
        var song = NewSong(mp3Path: null);

        context.SongMetadata.Add(song);
        await context.SaveChangesAsync();

        Assert.That(await StampOfAsync(song.Id), Is.Null);
    }

    [Test]
    public async Task ADraftIsStampedAtTheMomentItIsEnabled()
    {
        // The transition that matters, and the one no single code path owns - a song goes public
        // from the upload pipeline, from the transcode callback, from the admin page and from
        // SongStatusService. That is why this is an interceptor and not a line in one of them.
        int songId;

        await using (var create = _harness.NewContextWithPublicationStamp())
        {
            var draft = NewSong(enabled: false);
            create.SongMetadata.Add(draft);
            await create.SaveChangesAsync();
            songId = draft.Id;
        }

        Assert.That(await StampOfAsync(songId), Is.Null, "Precondition: the draft is unstamped.");

        await using (var publish = _harness.NewContextWithPublicationStamp())
        {
            var draft = await publish.SongMetadata.SingleAsync(song => song.Id == songId);
            draft.IsEnabled = true;
            await publish.SaveChangesAsync();
        }

        Assert.That(await StampOfAsync(songId), Is.Not.Null);
    }

    [Test]
    public async Task AnAlreadyStampedSongIsNeverStampedAgain()
    {
        // Stamped exactly once, ever. Re-stamping on a later edit is what would turn a re-crop, a
        // lyrics change or a title fix into a second "new release" for every follower.
        var published = ArtistFollowTestHarness.SeededSongPublishedAtUtc;
        var songId = _harness.AddSong("Ocean Road", firstPublishedAtUtc: published);

        await using (var edit = _harness.NewContextWithPublicationStamp())
        {
            var song = await edit.SongMetadata.SingleAsync(row => row.Id == songId);
            song.SongTitle = "Ocean Road (Remastered)";
            await edit.SaveChangesAsync();
        }

        Assert.That(await StampOfAsync(songId), Is.EqualTo(published));
    }

    [Test]
    public async Task WithdrawingAndRestoringASongKeepsItsOriginalPublicationDate()
    {
        // Otherwise pulling a song for a re-encode and putting it back would announce it twice.
        var published = ArtistFollowTestHarness.SeededSongPublishedAtUtc;
        var songId = _harness.AddSong("Ocean Road", firstPublishedAtUtc: published);

        foreach (var enabled in new[] { false, true })
        {
            await using var context = _harness.NewContextWithPublicationStamp();
            var song = await context.SongMetadata.SingleAsync(row => row.Id == songId);
            song.IsEnabled = enabled;
            await context.SaveChangesAsync();
        }

        Assert.That(await StampOfAsync(songId), Is.EqualTo(published));
    }
}
