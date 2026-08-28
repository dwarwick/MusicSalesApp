using System;
using System.Collections.Generic;
using System.Linq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The manifest rewrite is where a stored package becomes a specific listener's permission to play.
///
/// <para>
/// Every assertion here is on <see cref="HlsManifestBuilder.Rewrite"/> rather than on the service,
/// because the rules that matter - key substitution, absolute segment URLs, and where a preview
/// stops - are a pure function of the manifest text. Testing them through a blob client would test
/// the blob client.
/// </para>
/// </summary>
[TestFixture]
public class HlsManifestBuilderTests
{
    private const string KeyUri = "https://streamtunes.net/api/stream/42/key?t=abc123";

    private static readonly Uri SegmentBase =
        new("https://acct.blob.core.windows.net/musicstreaming/0123456789abcdef0123456789abcdef/");

    /// <summary>Six-second segments, as the packager produces. Ten of them is a minute.</summary>
    private static string RawManifest(int segmentCount, double segmentSeconds = 6.0)
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("#EXTM3U");
        lines.AppendLine("#EXT-X-VERSION:3");
        lines.AppendLine("#EXT-X-TARGETDURATION:6");
        lines.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        lines.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        lines.AppendLine(
            $"#EXT-X-KEY:METHOD=AES-128,URI=\"{HlsPackagePaths.KeyUriPlaceholder}\",IV=0x0123456789abcdef0123456789abcdef");

        for (var i = 0; i < segmentCount; i++)
        {
            lines.AppendLine($"#EXTINF:{segmentSeconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},");
            lines.AppendLine(HlsPackagePaths.SegmentFileName(i));
        }

        lines.AppendLine("#EXT-X-ENDLIST");
        return lines.ToString();
    }

    private static string[] SegmentLines(string manifest)
        => manifest.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

    [Test]
    public void Rewrite_ReplacesThePlaceholderKeyUriWithTheTokenisedOne()
    {
        var result = HlsManifestBuilder.Rewrite(RawManifest(3), SegmentBase, KeyUri, previewLimit: null);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain($"URI=\"{KeyUri}\""));

            // The stored placeholder reaching a player would mean it had no way to fetch a key at
            // all - the failure is silent up to the point playback simply does not start.
            Assert.That(result, Does.Not.Contain(HlsPackagePaths.KeyUriPlaceholder));
        });
    }

    [Test]
    public void Rewrite_PreservesTheEncryptionMethodAndIv()
    {
        var result = HlsManifestBuilder.Rewrite(RawManifest(3), SegmentBase, KeyUri, previewLimit: null);

        // Only the URI is substituted. Losing METHOD or IV would leave a manifest that either does
        // not decrypt or decrypts to noise.
        Assert.That(result, Does.Contain("METHOD=AES-128"));
        Assert.That(result, Does.Contain("IV=0x0123456789abcdef0123456789abcdef"));
    }

    [Test]
    public void Rewrite_MakesEverySegmentAnAbsoluteStorageUrl()
    {
        var result = HlsManifestBuilder.Rewrite(RawManifest(3), SegmentBase, KeyUri, previewLimit: null);

        var segments = SegmentLines(result);

        Assert.That(segments, Has.Length.EqualTo(3));
        Assert.That(segments[0], Is.EqualTo(SegmentBase + "seg-000.ts"));
        Assert.That(segments[2], Is.EqualTo(SegmentBase + "seg-002.ts"));
    }

    [Test]
    public void Rewrite_ForAFullAccessListener_KeepsEverySegment()
    {
        var result = HlsManifestBuilder.Rewrite(RawManifest(40), SegmentBase, KeyUri, previewLimit: null);

        Assert.That(SegmentLines(result), Has.Length.EqualTo(40));
        Assert.That(result.TrimEnd(), Does.EndWith("#EXT-X-ENDLIST"));
    }

    /// <summary>
    /// The preview is enforced by <em>omission</em>, and that is the whole point of doing it here.
    ///
    /// <para>
    /// Until now the 60-second cap lived in JavaScript: a non-subscriber was sent the entire file
    /// and asked politely to stop. A truncated manifest does not describe the rest of the song at
    /// all, so there is nothing to skip past, seek into, or read out of the network tab.
    /// </para>
    /// </summary>
    [Test]
    public void Rewrite_ForAPreviewListener_DescribesOnlyThePreviewWindow()
    {
        var result = HlsManifestBuilder.Rewrite(
            RawManifest(40),
            SegmentBase,
            KeyUri,
            TimeSpan.FromSeconds(60));

        var segments = SegmentLines(result);

        Assert.Multiple(() =>
        {
            // Ten six-second segments are exactly the minute, so the cut lands cleanly on the
            // boundary and there is nothing to round.
            Assert.That(segments, Has.Length.EqualTo(10));
            Assert.That(segments.Last(), Is.EqualTo(SegmentBase + "seg-009.ts"));

            // Nothing beyond the window is named anywhere in the response.
            Assert.That(result, Does.Not.Contain("seg-010.ts"));
            Assert.That(result, Does.Not.Contain("seg-039.ts"));
        });
    }

    /// <summary>
    /// When the segment length does not divide the preview evenly, the cut rounds <em>up</em>.
    ///
    /// <para>
    /// The decision is made on time already emitted rather than on where the next segment ends, so a
    /// preview always covers at least the full window rather than stopping short of it. Rounding
    /// down would hand a "60 second preview" that ran out at 56, and the player's own cap trims the
    /// overshoot anyway.
    /// </para>
    /// </summary>
    [Test]
    public void Rewrite_WhenSegmentsDoNotDivideThePreviewEvenly_RoundsUp()
    {
        // 7-second segments: eight of them reach 56s, so a ninth is needed to cover the minute.
        var result = HlsManifestBuilder.Rewrite(
            RawManifest(40, segmentSeconds: 7.0),
            SegmentBase,
            KeyUri,
            TimeSpan.FromSeconds(60));

        var segments = SegmentLines(result);

        Assert.That(segments, Has.Length.EqualTo(9));
        Assert.That(segments.Length * 7, Is.GreaterThanOrEqualTo(60));
    }

    [Test]
    public void Rewrite_ForAPreviewListener_StillEndsWithEndlist()
    {
        var result = HlsManifestBuilder.Rewrite(
            RawManifest(40),
            SegmentBase,
            KeyUri,
            TimeSpan.FromSeconds(60));

        // Without ENDLIST a player treats a VOD playlist as live and keeps re-fetching it forever,
        // waiting for segments that are never coming.
        Assert.That(result.TrimEnd(), Does.EndWith("#EXT-X-ENDLIST"));
    }

    [Test]
    public void Rewrite_WhenTruncating_DropsTheExtinfOfTheSegmentItDropped()
    {
        var result = HlsManifestBuilder.Rewrite(
            RawManifest(40),
            SegmentBase,
            KeyUri,
            TimeSpan.FromSeconds(60));

        var extinfCount = result.Split('\n').Count(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal));

        // One #EXTINF per emitted segment and no more. A trailing #EXTINF with no URI after it is a
        // malformed playlist, and players differ on whether they tolerate it.
        Assert.That(extinfCount, Is.EqualTo(SegmentLines(result).Length));
    }

    [Test]
    public void Rewrite_WhenTheSongIsShorterThanThePreview_KeepsItWhole()
    {
        // Five six-second segments is half a minute, so a preview limit changes nothing.
        var result = HlsManifestBuilder.Rewrite(
            RawManifest(5),
            SegmentBase,
            KeyUri,
            TimeSpan.FromSeconds(60));

        Assert.That(SegmentLines(result), Has.Length.EqualTo(5));
    }

    [Test]
    public void Rewrite_AlwaysEmitsAtLeastOneSegment()
    {
        // A pathological preview limit must not produce an empty playlist, which players report as
        // a decode error rather than as a short song.
        var result = HlsManifestBuilder.Rewrite(
            RawManifest(40),
            SegmentBase,
            KeyUri,
            TimeSpan.Zero);

        Assert.That(SegmentLines(result), Has.Length.EqualTo(1));
    }

    [Test]
    public void Rewrite_HandlesCarriageReturnsFromFfmpegOnWindows()
    {
        // FFmpeg on Windows writes CRLF. A stray \r left on a segment name would become part of the
        // URL and 404 every request.
        var raw = RawManifest(3).Replace("\n", "\r\n");

        var result = HlsManifestBuilder.Rewrite(raw, SegmentBase, KeyUri, previewLimit: null);

        Assert.That(SegmentLines(result), Has.All.Not.Contains("\r"));
        Assert.That(SegmentLines(result)[0], Is.EqualTo(SegmentBase + "seg-000.ts"));
    }

    /// <summary>
    /// The streaming container is private, so every segment URL has to carry a read SAS.
    ///
    /// <para>
    /// A missed one 403s at the player, and because segments are fetched lazily it would not fail
    /// at the start of a song but part-way through it.
    /// </para>
    /// </summary>
    [Test]
    public void Rewrite_StampsTheSegmentSasOntoEverySegment()
    {
        var result = HlsManifestBuilder.Rewrite(RawManifest(3), SegmentBase, KeyUri, null, SasFor);

        var segments = SegmentLines(result);

        Assert.That(segments, Has.Length.EqualTo(3));
        Assert.That(segments[0], Is.EqualTo(SegmentBase + "seg-000.ts?" + SasFor("seg-000.ts")));
        Assert.That(segments[2], Is.EqualTo(SegmentBase + "seg-002.ts?" + SasFor("seg-002.ts")));
    }

    /// <summary>
    /// Each segment gets the credential minted for <em>that</em> segment.
    ///
    /// <para>
    /// This is what makes a truncated preview mean anything. There is one content key per song, so a
    /// preview listener holds the same key a subscriber does and the truncation is the only thing
    /// limiting them - but segment names are deterministic, so with one credential covering them all
    /// the omitted segments would be a guessable filename away. Passing the factory the file name and
    /// using what it returns for that file is the whole mechanism.
    /// </para>
    /// </summary>
    [Test]
    public void Rewrite_AsksForACredentialPerSegmentRatherThanOneForAllOfThem()
    {
        var asked = new List<string>();

        var result = HlsManifestBuilder.Rewrite(
            RawManifest(3),
            SegmentBase,
            KeyUri,
            previewLimit: null,
            fileName =>
            {
                asked.Add(fileName);
                return SasFor(fileName);
            });

        Assert.Multiple(() =>
        {
            Assert.That(asked, Is.EqualTo(new[] { "seg-000.ts", "seg-001.ts", "seg-002.ts" }));

            // No segment carries another's signature.
            Assert.That(SegmentLines(result)[0], Does.Not.Contain(SasFor("seg-001.ts")));
        });
    }

    /// <summary>
    /// A truncated preview must not hand out credentials for the segments it withheld.
    ///
    /// <para>
    /// The point of asking per segment is that the request never happens for a segment the listener
    /// is not entitled to - so there is nothing for them to replay against the omitted blobs.
    /// </para>
    /// </summary>
    [Test]
    public void Rewrite_WhenTruncated_NeverMintsACredentialForAWithheldSegment()
    {
        var asked = new List<string>();

        HlsManifestBuilder.Rewrite(
            RawManifest(40),
            SegmentBase,
            KeyUri,
            TimeSpan.FromSeconds(60),
            fileName =>
            {
                asked.Add(fileName);
                return SasFor(fileName);
            });

        Assert.That(asked, Has.Count.LessThan(40), "a preview must not sign the whole song");
        Assert.That(asked, Has.None.EqualTo("seg-039.ts"));
    }

    [Test]
    public void Rewrite_DoesNotStampTheSasOntoTheKeyUri()
    {
        var result = HlsManifestBuilder.Rewrite(RawManifest(3), SegmentBase, KeyUri, null, SasFor);

        // The key comes from this app, not from storage. Appending a storage SAS to it would at best
        // be noise and at worst break the token the key endpoint validates.
        Assert.That(result, Does.Contain($"URI=\"{KeyUri}\""));
        Assert.That(result, Does.Not.Contain($"{KeyUri}?"));
    }

    [Test]
    public void Rewrite_WithNoSas_LeavesSegmentUrlsBare()
    {
        // What an unsigned manifest looks like. The builder still emits one rather than failing, so
        // the fault surfaces at the player instead of turning every request into a 500 - the SAS
        // provider has already logged the real cause.
        var result = HlsManifestBuilder.Rewrite(RawManifest(2), SegmentBase, KeyUri, null, segmentSasFactory: null);

        Assert.That(SegmentLines(result), Has.All.Not.Contains("?"));
    }

    /// <summary>A stand-in for a real signature, distinct per blob exactly as the signed one is.</summary>
    private static string SasFor(string segmentFileName)
        => $"sv=2024-01-01&se=2026-01-01&sr=b&sig=sig-for-{segmentFileName}";
}
