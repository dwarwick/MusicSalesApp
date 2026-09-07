using System;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// Content keys are the one secret in this system whose loss is unrecoverable: without them the
/// packaged catalogue is noise and every song has to be re-encoded.
/// </summary>
[TestFixture]
public class HlsContentKeyProtectorTests
{
    private const int SongId = 4242;

    private static HlsContentKeyProtector Create(string wrappingKey = null)
        => new(
            Options.Create(new HlsOptions
            {
                ContentKeyWrappingKey = wrappingKey ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }),
            Mock.Of<ILogger<HlsContentKeyProtector>>());

    [Test]
    public void ProtectThenUnprotect_ReturnsTheSameKey()
    {
        var protector = Create();
        var key = HlsContentKeyProtector.CreateContentKey();

        var recovered = protector.Unprotect(SongId, protector.Protect(SongId, key));

        Assert.That(recovered, Is.EqualTo(key));
    }

    [Test]
    public void CreateContentKey_ProducesSixteenBytes()
        // AES-128 is what HLS's EXT-X-KEY METHOD=AES-128 means. Any other length would be rejected
        // by the packager rather than producing a subtly broken package.
        => Assert.That(HlsContentKeyProtector.CreateContentKey(), Has.Length.EqualTo(16));

    [Test]
    public void Protect_ProducesADifferentBlobEachTime()
    {
        var protector = Create();
        var key = HlsContentKeyProtector.CreateContentKey();

        var first = protector.Protect(SongId, key);
        var second = protector.Protect(SongId, key);

        // A fresh nonce per wrap. Identical ciphertext for identical input would leak which songs
        // share a key to anyone who could read the column.
        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public void Protect_FitsTheColumn()
    {
        var protector = Create();

        // SongMetadata.HlsKeyProtected is nvarchar(256). Silently truncating a wrapped key would
        // lose the catalogue, so the size relationship is worth pinning rather than assuming.
        Assert.That(
            protector.Protect(SongId, HlsContentKeyProtector.CreateContentKey()).Length,
            Is.LessThan(256));
    }

    /// <summary>
    /// The song id is authenticated as associated data, so a wrapped key is only valid for the row
    /// it was written to. Moving one between rows fails closed rather than decrypting another song.
    /// </summary>
    [Test]
    public void Unprotect_WithADifferentSongId_Throws()
    {
        var protector = Create();
        var wrapped = protector.Protect(SongId, HlsContentKeyProtector.CreateContentKey());

        Assert.Catch<CryptographicException>(() => protector.Unprotect(SongId + 1, wrapped));
    }

    [Test]
    public void Unprotect_WithATamperedBlob_Throws()
    {
        var protector = Create();
        var wrapped = protector.Protect(SongId, HlsContentKeyProtector.CreateContentKey());

        // Flip a character in the MIDDLE of the payload. GCM authenticates, so this is detected
        // rather than producing a wrong-but-plausible key that would decrypt every segment to
        // noise.
        //
        // Not near the end, which is where this test used to flip: the trailing characters of a
        // Base64 string can carry padding bits that decode to nothing, so a flip there sometimes
        // produced byte-identical input and no exception. The content key is random per run, so
        // the payload length - and therefore whether the flip landed on padding - varied, and the
        // test failed perhaps one run in several.
        var middle = wrapped.Length / 2;
        var tampered = wrapped[..middle] + (wrapped[middle] == 'A' ? 'B' : 'A') + wrapped[(middle + 1)..];

        Assert.Catch<CryptographicException>(() => protector.Unprotect(SongId, tampered));
    }

    [Test]
    public void Unprotect_WithADifferentWrappingKey_Throws()
    {
        var wrapped = Create().Protect(SongId, HlsContentKeyProtector.CreateContentKey());

        // What a botched key rotation looks like: the rows still hold v1 payloads wrapped under the
        // old key. It must fail loudly here rather than serve a wrong key to a player.
        Assert.Catch<CryptographicException>(() => Create().Unprotect(SongId, wrapped));
    }

    [Test]
    public void Unprotect_WithNothingStored_Throws()
        => Assert.Catch<CryptographicException>(() => Create().Unprotect(SongId, null));

    [Test]
    public void Protect_CarriesAVersionPrefix()
    {
        // Rotation is a database re-wrap, not a re-encode, and the prefix is what makes it possible
        // to run both wrapping keys at once while rows migrate.
        Assert.That(
            Create().Protect(SongId, HlsContentKeyProtector.CreateContentKey()),
            Does.StartWith("v1."));
    }

    [Test]
    public void Constructor_WithAWrongLengthKey_ThrowsWithAUsefulMessage()
    {
        var tooShort = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var ex = Assert.Throws<InvalidOperationException>(() => Create(tooShort));

        Assert.That(ex.Message, Does.Contain("32"));
    }

    [Test]
    public void Constructor_WithNoKeyConfigured_DoesNotThrow()
    {
        // A site with no wrapping key must still start and serve everything that is not encrypted
        // playback. The endpoints that need it fail individually and say so.
        var protector = new HlsContentKeyProtector(
            Options.Create(new HlsOptions()),
            Mock.Of<ILogger<HlsContentKeyProtector>>());

        Assert.That(protector.IsConfigured, Is.False);
        Assert.Throws<InvalidOperationException>(
            () => protector.Protect(SongId, HlsContentKeyProtector.CreateContentKey()));
    }
}
