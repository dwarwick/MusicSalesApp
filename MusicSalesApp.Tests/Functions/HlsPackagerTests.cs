using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Functions.Audio;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Functions;

/// <summary>
/// The encrypted-HLS packaging step, and specifically what it leaves behind.
///
/// <para>
/// This is the only FFmpeg call in the app whose working directory holds a secret: FFmpeg has to
/// read the AES-128 content key from a plaintext file on disk, so the directory is key material for
/// as long as it exists.
/// </para>
/// </summary>
[TestFixture]
public class HlsPackagerTests
{
    private static HlsPackager Create() => new(NullLogger<HlsPackager>.Instance);

    private static string[] PackagingDirectories()
        => Directory.GetDirectories(Path.GetTempPath(), "hls-*");

    /// <summary>
    /// A failed packaging run must not leave the plaintext content key on the worker.
    ///
    /// <para>
    /// The failure results deliberately carry no <c>OutputDirectory</c>, so the caller has no path to
    /// clean up by even if it wanted to — its <c>finally</c> receives null and does nothing. That
    /// made every failure path leave a <c>content.key</c> behind on an instance whose disk a later
    /// execution reuses. Cleanup therefore belongs to whoever created the directory.
    /// </para>
    ///
    /// <para>
    /// Meaningful whether or not FFmpeg is present: without it the packager fails before creating
    /// anything, and with it the source below is not decodable, so either way nothing may survive.
    /// </para>
    /// </summary>
    [Test]
    public async Task PackageAsync_WhenPackagingFails_LeavesNoKeyMaterialBehind()
    {
        var before = PackagingDirectories();

        var source = Path.Combine(Path.GetTempPath(), $"not-audio-{Guid.NewGuid():N}.mp3");
        await File.WriteAllTextAsync(source, "this is not an audio file");

        try
        {
            var result = await Create().PackageAsync(
                source,
                HlsPackager.CreateKeyMaterial().Key,
                HlsPackager.CreateKeyMaterial().Iv);

            Assert.That(result.Status, Is.Not.EqualTo(AudioDecodeStatus.Playable));

            var leaked = PackagingDirectories().Except(before).ToArray();

            Assert.That(
                leaked,
                Is.Empty,
                "a failed run left its packaging directory behind, and that directory contains the "
                + "song's plaintext AES key: " + string.Join(", ", leaked));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Test]
    public async Task PackageAsync_WithAMissingSource_IsUnplayableRatherThanInconclusive()
    {
        // The file is gone, which is the source's problem and not the worker's - so it must not be
        // retried onto another instance forever.
        var result = await Create().PackageAsync(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.mp3"),
            HlsPackager.CreateKeyMaterial().Key,
            HlsPackager.CreateKeyMaterial().Iv);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AudioDecodeStatus.Unplayable));
            Assert.That(result.FailureCode, Is.EqualTo("MissingInput"));
        });
    }

    /// <summary>
    /// Pins the reason the packager cleans up after itself rather than the caller doing it.
    /// </summary>
    [Test]
    public void AFailureResultCarriesNoOutputDirectoryForACallerToCleanUp()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HlsPackageResult.Unplayable("code", "why").OutputDirectory, Is.Null);
            Assert.That(HlsPackageResult.Inconclusive("code", "why").OutputDirectory, Is.Null);
        });
    }

    [Test]
    public void CreateKeyMaterial_ProducesDistinct128BitKeyAndIv()
    {
        var (key, iv) = HlsPackager.CreateKeyMaterial();
        var (otherKey, _) = HlsPackager.CreateKeyMaterial();

        Assert.Multiple(() =>
        {
            Assert.That(key, Has.Length.EqualTo(16), "AES-128");
            Assert.That(iv, Has.Length.EqualTo(16));
            Assert.That(key, Is.Not.EqualTo(otherKey), "keys must not repeat across songs");
        });
    }
}
