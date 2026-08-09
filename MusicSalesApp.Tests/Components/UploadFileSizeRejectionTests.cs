using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Components.Pages.Creator;

namespace MusicSalesApp.Tests.Components;

/// <summary>
/// Rejecting an over-cap file before any stream is opened.
///
/// <para>
/// This exists because of a real crash. <c>IBrowserFile.OpenReadStream(maxAllowedSize)</c> throws
/// <see cref="IOException"/> the instant a file exceeds the cap, the buffering loop only caught
/// <see cref="InvalidDataException"/>, and an IOException escaping a Blazor event handler takes the
/// circuit down. A creator who picked one file 2 MB over the limit lost the whole batch to a crash
/// with nothing telling them which file or why - and the server-side gate in
/// <c>SongUploadJobService</c> never ran, because the client-side cap fires first.
/// </para>
/// </summary>
[TestFixture]
public class UploadFileSizeRejectionTests
{
    private const long TenMB = 10 * 1024 * 1024;
    private const long TwoMB = 2 * 1024 * 1024;

    /// <summary>Only Name and Size are read, which is the point - no stream is ever opened.</summary>
    private sealed class StubBrowserFile(string name, long size) : IBrowserFile
    {
        public string Name { get; } = name;
        public long Size { get; } = size;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public string ContentType => "application/octet-stream";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "The size check must never open a stream - opening one is exactly what used to crash the circuit.");
    }

    private static List<string> Find(
        IEnumerable<IBrowserFile> audio,
        IEnumerable<IBrowserFile> images,
        long maxAudio = TenMB,
        long maxImage = TwoMB)
        => UploadFilesModel.FindOversizedFiles(audio, images, maxAudio, maxImage);

    [Test]
    public void AnOversizedAudioFile_IsNamed()
    {
        var result = Find([new StubBrowserFile("Big.wav", TenMB + 1)], []);

        Assert.That(result, Is.EqualTo(new[] { "Big.wav" }));
    }

    [Test]
    public void AnOversizedImage_IsNamed_AgainstItsOwnCap()
    {
        // The image cap is far lower than the audio one, so a 3 MB cover is over while a 3 MB song
        // is not. Checking both against a single limit would let large art straight through.
        var result = Find(
            [new StubBrowserFile("Song.wav", 3 * 1024 * 1024)],
            [new StubBrowserFile("Cover.png", 3 * 1024 * 1024)]);

        Assert.That(result, Is.EqualTo(new[] { "Cover.png" }));
    }

    [Test]
    public void EveryOversizedFileIsNamed_NotJustTheFirst()
    {
        // The creator should be able to fix the whole selection in one go rather than re-dropping
        // and discovering the next offender each time.
        var result = Find(
            [
                new StubBrowserFile("A.wav", TenMB + 1),
                new StubBrowserFile("B.wav", 1024),
                new StubBrowserFile("C.wav", TenMB * 2)
            ],
            [new StubBrowserFile("D.png", TwoMB + 1)]);

        Assert.That(result, Is.EqualTo(new[] { "A.wav", "C.wav", "D.png" }));
    }

    [Test]
    public void AFileExactlyAtTheCap_IsAllowed()
    {
        // Boundary matters: OpenReadStream throws only when the size *exceeds* maxAllowedSize, so
        // rejecting the exact value here would refuse a file the old path would have accepted.
        var result = Find(
            [new StubBrowserFile("Exact.wav", TenMB)],
            [new StubBrowserFile("Exact.png", TwoMB)]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void AnAcceptableSelection_ProducesNothing()
        => Assert.That(
            Find([new StubBrowserFile("Song.wav", 1024)], [new StubBrowserFile("Cover.png", 1024)]),
            Is.Empty);

    [Test]
    public void AnEmptySelection_IsHandled()
        => Assert.That(Find([], []), Is.Empty);

    [Test]
    public void ANullCollection_IsHandled()
        => Assert.That(Find(null, null), Is.Empty);

    [TestCase(0L)]
    [TestCase(-1L)]
    public void AnUnsetCap_DoesNotRejectEverything(long cap)
    {
        // The caps come from admin settings. A missing or nonsensical row must not turn every upload
        // into a rejection - the pipeline still has its own server-side gate behind this.
        var result = Find([new StubBrowserFile("Song.wav", long.MaxValue / 2)], [], maxAudio: cap);

        Assert.That(result, Is.Empty);
    }
}
