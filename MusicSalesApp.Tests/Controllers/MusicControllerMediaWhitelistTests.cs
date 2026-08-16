using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

/// <summary>
/// The public media endpoint is the only thing standing between an arbitrary blob path on the wire
/// and blob storage, so the rendition support added to it gets its own fixture.
/// </summary>
[TestFixture]
public class MusicControllerMediaWhitelistTests
{
    private const string Guid32 = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b";
    private static readonly string CoverArt = $"{Guid32}/{Guid32}-coverart.jpg";
    private static readonly string Playback = $"{Guid32}/{Guid32}-music.mp3";
    private static readonly string LyricsTimings = $"{Guid32}/{Guid32}-lyrics.json";
    private static readonly string LyricsLrc = $"{Guid32}/{Guid32}-lyrics.lrc";

    private Mock<IAzureStorageService> _storage;
    private Mock<ISongMetadataService> _metadata;
    private Mock<ISongLyricsService> _lyrics;
    private MusicController _controller;

    [SetUp]
    public void SetUp()
    {
        _storage = new Mock<IAzureStorageService>();
        _metadata = new Mock<ISongMetadataService>();
        _lyrics = new Mock<ISongLyricsService>();

        // The existing tests all exercise Stream(), which never reaches SAS generation.
        // GetStreamUrl() does, and a null Uri from the mock would surface as a
        // NullReferenceException that looks like a controller bug rather than a fixture gap.
        _storage.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://storage.test/blob"));

        _controller = new MusicController(
            _storage.Object,
            Mock.Of<ISubscriptionService>(),
            Mock.Of<IStreamCountService>(),
            _metadata.Object,
            Mock.Of<ISongLikeService>(),
            Mock.Of<ICreatorPersonaService>(),
            Mock.Of<IReportedSongService>(),
            Mock.Of<IAppSettingsService>(),
            Mock.Of<IMobileSongMapper>(),
            MockUserManager(),
            Mock.Of<ICreatorService>(),
            Mock.Of<Microsoft.AspNetCore.Authorization.IAuthorizationService>(),
            Mock.Of<IImageVariantCoordinator>(),
            _lyrics.Object,
            Mock.Of<ILogger<MusicController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static UserManager<ApplicationUser> MockUserManager()
        => new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null).Object;

    /// <summary>Registers a song reachable by either its cover art or its playback path.</summary>
    private void RegisterSong(bool isActive = true, bool isEnabled = true, string variantWidths = null)
    {
        var song = new SongMetadata
        {
            Id = 1,
            Mp3BlobPath = Playback,
            ImageBlobPath = CoverArt,
            CoverArtVariantWidths = variantWidths,
            IsActive = isActive,
            IsEnabled = isEnabled
        };

        _metadata.Setup(s => s.GetByBlobPathAsync(CoverArt)).ReturnsAsync(song);
        _metadata.Setup(s => s.GetByBlobPathAsync(Playback)).ReturnsAsync(song);
    }

    private void HaveBlob(string path, byte[] content = null)
        => _storage.Setup(s => s.OpenReadAsync(path))
            .ReturnsAsync(() => new MemoryStream(content ?? new byte[] { 1, 2, 3, 4 }));

    private void HaveNoBlob(string path)
        => _storage.Setup(s => s.OpenReadAsync(path)).ReturnsAsync((Stream)null);

    [Test]
    public async Task Stream_ServesARenditionOfRegisteredCoverArt()
    {
        RegisterSong();
        var variant = ImageVariantPaths.Variant(CoverArt, 320);
        HaveBlob(variant);

        var result = await _controller.Stream(variant);

        Assert.That(result, Is.InstanceOf<FileStreamResult>());
        Assert.That(((FileStreamResult)result).ContentType, Is.EqualTo("image/webp"));
    }

    [Test]
    public async Task Stream_RejectsARenditionOfTheAudioBlob()
    {
        // Without the "base must equal ImageBlobPath" check, appending .w320.webp to the mp3 path
        // would be a way to ask the server to read an arbitrary sibling blob.
        RegisterSong();
        var forged = ImageVariantPaths.Variant(Playback, 320);
        HaveBlob(forged);

        Assert.That(await _controller.Stream(forged), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Stream_RejectsARenditionWhoseMasterIsNotRegisteredAtAll()
    {
        _metadata.Setup(s => s.GetByBlobPathAsync(It.IsAny<string>())).ReturnsAsync((SongMetadata)null);
        var variant = ImageVariantPaths.Variant("someone-elses/secret.jpg", 320);
        HaveBlob(variant);

        Assert.That(await _controller.Stream(variant), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Stream_ServesARenditionAtANonLadderWidthThatTheSongActuallyRecorded()
    {
        // A master narrower than the smallest rung yields one rendition at its own width. It is that
        // song's only srcset candidate, so refusing it because 100 is not on the ladder renders a
        // broken image - a browser does not fall back to src when a srcset candidate 404s.
        RegisterSong(variantWidths: "100");
        var variant = ImageVariantPaths.Variant(CoverArt, 100);
        HaveBlob(variant);

        Assert.That(await _controller.Stream(variant), Is.InstanceOf<FileStreamResult>());
    }

    [Test]
    public async Task Stream_RejectsANonLadderWidthTheSongNeverRecorded()
    {
        // Otherwise any integer would be a distinct cacheable URL for the same master.
        RegisterSong(variantWidths: "128,320");
        var variant = ImageVariantPaths.Variant(CoverArt, 999);
        HaveBlob(variant);

        Assert.That(await _controller.Stream(variant), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Stream_ServesALadderWidthEvenBeforeTheRowRecordsIt()
    {
        // Mid-backfill the blobs exist before the row is updated; consulting only the row would 404
        // renditions sitting right there in storage.
        RegisterSong(variantWidths: null);
        var variant = ImageVariantPaths.Variant(CoverArt, 320);
        HaveBlob(variant);

        Assert.That(await _controller.Stream(variant), Is.InstanceOf<FileStreamResult>());
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task Stream_RejectsRenditionsOfSongsThatAreNotPublic(bool isActive, bool isEnabled)
    {
        RegisterSong(isActive, isEnabled);
        var variant = ImageVariantPaths.Variant(CoverArt, 320);
        HaveBlob(variant);

        Assert.That(await _controller.Stream(variant), Is.InstanceOf<NotFoundResult>());
    }

    [TestCase(200)]
    [TestCase(2048)]
    [TestCase(1)]
    public async Task Stream_RejectsWidthsThisApplicationDoesNotGenerate(int width)
    {
        // Bounds what an unauthenticated caller can enumerate to the four ladder widths.
        RegisterSong();
        var variant = ImageVariantPaths.Variant(CoverArt, width);
        HaveBlob(variant);

        Assert.That(await _controller.Stream(variant), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Stream_WhenARenditionIsMissing_FallsBackToTheMasterRatherThan404()
    {
        // Mid-backfill, or restored from a backup taken before the backfill ran. A browser does not
        // try another srcset candidate on a 404 - it renders a broken image - so failing soft here
        // is what keeps the feature safe to deploy ahead of the backfill.
        RegisterSong();
        var variant = ImageVariantPaths.Variant(CoverArt, 640);
        HaveNoBlob(variant);
        HaveBlob(CoverArt);

        var result = await _controller.Stream(variant);

        Assert.That(result, Is.InstanceOf<FileStreamResult>());
        Assert.That(((FileStreamResult)result).ContentType, Is.EqualTo("image/jpeg"),
            "the fallback serves the master, so the content type must describe the master");
    }

    [Test]
    public async Task Stream_WhenBothTheRenditionAndTheMasterAreMissing_Is404()
    {
        RegisterSong();
        var variant = ImageVariantPaths.Variant(CoverArt, 640);
        HaveNoBlob(variant);
        HaveNoBlob(CoverArt);

        Assert.That(await _controller.Stream(variant), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Stream_StillServesTheMasterCoverArtAndPlaybackBlobs()
    {
        RegisterSong();
        HaveBlob(CoverArt);
        HaveBlob(Playback);

        Assert.Multiple(async () =>
        {
            Assert.That(await _controller.Stream(CoverArt), Is.InstanceOf<FileStreamResult>());
            Assert.That(await _controller.Stream(Playback), Is.InstanceOf<FileStreamResult>());
        });
    }

    [Test]
    public async Task Stream_DoesNotFallBackForANonRenditionPath()
    {
        // Only rendition requests get the master fallback; a missing master is still a 404.
        RegisterSong();
        HaveNoBlob(CoverArt);

        Assert.That(await _controller.Stream(CoverArt), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Stream_ServesRenditionsWithTheLongLivedImmutableCacheHeader()
    {
        RegisterSong();
        var variant = ImageVariantPaths.Variant(CoverArt, 128);
        HaveBlob(variant);

        await _controller.Stream(variant);

        Assert.That(
            _controller.Response.Headers["Cache-Control"].ToString(),
            Is.EqualTo("public,max-age=31536000,immutable"));
    }

    [Test]
    public async Task Stream_LooksUpTheMasterPathNotTheRenditionPath()
    {
        // The rendition path spells the master out in full, so the existing blob-path lookup works
        // untouched - no second query and no new index.
        RegisterSong();
        var variant = ImageVariantPaths.Variant(CoverArt, 320);
        HaveBlob(variant);

        await _controller.Stream(variant);

        _metadata.Verify(s => s.GetByBlobPathAsync(CoverArt), Times.Once);
        _metadata.Verify(s => s.GetByBlobPathAsync(variant), Times.Never);
    }

    // -----------------------------------------------------------------
    // Lyrics artifacts.
    //
    // These resolve differently from everything else here: their paths live on SongLyrics, not on
    // SongMetadata, so the song-by-blob-path lookup cannot find them at all. The branch exists to
    // route them to the lyrics row - and the whole point of that row is that a path alone does NOT
    // establish access, because withheld and published timings occupy the identical path.
    // -----------------------------------------------------------------

    [Test]
    public async Task PublishedTimingsAreServed()
    {
        _lyrics.Setup(s => s.IsPubliclyReadableAsync(LyricsTimings, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.GetStreamUrl(LyricsTimings);

        Assert.That(result, Is.Not.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task WithheldTimingsAre404()
    {
        // The single most important assertion in this file, and it matters more now than it did.
        // Unpublished timings sit at exactly the path published ones would, so nothing about the
        // request distinguishes them - only the row does. And since alignment no longer publishes
        // anything, EVERY freshly aligned song is in this state until its creator has listened to it
        // and pressed Publish. This route is what keeps that promise.
        _lyrics.Setup(s => s.IsPubliclyReadableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.GetStreamUrl(LyricsTimings);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task WithheldTimingsAre404OnTheStreamRouteAsWell()
    {
        // Both public routes consult the same gate, and both have to be asserted: GetStreamUrl mints
        // a SAS and Stream proxies the bytes, so either one leaking is a leak. Testing only the first
        // would leave the second free to regress silently.
        _lyrics.Setup(s => s.IsPubliclyReadableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.Stream(LyricsTimings);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task ALyricsPathIsNeverResolvedAgainstTheSongTable()
    {
        // Routing, not authorisation. If this ever fell through to the song lookup it would 404 for
        // a different reason - and the day someone "fixes" that 404 by adding the path to
        // SongMetadata, the status gate disappears silently.
        _lyrics.Setup(s => s.IsPubliclyReadableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _controller.GetStreamUrl(LyricsTimings);

        _metadata.Verify(s => s.GetByBlobPathAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task TheLrcExportFollowsTheSameGate()
    {
        _lyrics.Setup(s => s.IsPubliclyReadableAsync(LyricsLrc, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.GetStreamUrl(LyricsLrc);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task TheCreatorsPastedTextIsNotReachableAtAll()
    {
        // Deliberately not a lyrics ARTIFACT: the .txt is the creator's working copy and has no
        // public route. It falls through to the song lookup, which does not know the path, and 404s.
        var pastedText = $"{Guid32}/{Guid32}-lyrics.txt";

        var result = await _controller.GetStreamUrl(pastedText);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
        _lyrics.Verify(
            s => s.IsPubliclyReadableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ALegacySchemeLyricsPathIsRoutedToo()
    {
        // Legacy songs have no GUID folder, so their timings are named from the song id. The suffix
        // is what the two schemes have in common, and what the routing test keys off.
        const string legacy = "Night Drive/42-lyrics.json";

        _lyrics.Setup(s => s.IsPubliclyReadableAsync(legacy, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.GetStreamUrl(legacy);

        Assert.That(result, Is.Not.InstanceOf<NotFoundResult>());
    }
}
