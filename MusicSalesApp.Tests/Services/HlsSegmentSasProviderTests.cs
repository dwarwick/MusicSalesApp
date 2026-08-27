using System;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The credential every segment URL carries, now that the streaming container is private.
///
/// <para>
/// The property that matters most here is scope. A song has one content key, so a preview listener
/// necessarily receives the same key a subscriber does; the only thing that keeps their preview a
/// preview is that they cannot fetch the segments their manifest left out. That holds only while
/// each SAS is signed for one blob.
/// </para>
/// </summary>
[TestFixture]
public class HlsSegmentSasProviderTests
{
    private const string SegmentPath = "0123456789abcdef0123456789abcdef/seg-000.ts";

    /// <summary>A key-based connection string, so the client can actually sign. Not a real account.</summary>
    private const string ConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=devstoreaccount1;"
        + "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
        + "EndpointSuffix=core.windows.net";

    private static HlsSegmentSasProvider Create(HlsOptions options = null)
    {
        var factory = new Mock<IBlobContainerFactory>();
        factory.Setup(f => f.GetStreamingContainer())
            .Returns(new BlobContainerClient(ConnectionString, "musicstreaming-test"));

        return new HlsSegmentSasProvider(
            factory.Object,
            Options.Create(options ?? new HlsOptions()),
            Mock.Of<ILogger<HlsSegmentSasProvider>>());
    }

    [Test]
    public void GetReadSasQuery_ProducesASignedReadOnlyQuery()
    {
        var query = Create().GetReadSasQuery(SegmentPath);

        Assert.Multiple(() =>
        {
            Assert.That(query, Is.Not.Null.And.Not.Empty);

            // No leading '?': the manifest builder adds the separator, and a double '?' would make
            // every segment URL malformed.
            Assert.That(query, Does.Not.StartWith("?"));

            Assert.That(query, Does.Contain("sig="));
            Assert.That(query, Does.Contain("se="));

            // Read only. A write or delete permission here would let anyone holding a segment URL
            // modify the catalogue.
            Assert.That(query, Does.Contain("sp=r"));
        });
    }

    /// <summary>
    /// The regression test for the free preview being defeatable.
    ///
    /// <para>
    /// Segment names are deterministic — <c>seg-000.ts</c>, <c>seg-001.ts</c>, … — and the package
    /// folder appears in the URL of every segment the listener legitimately received. So with a
    /// container-scoped credential (<c>sr=c</c>), a non-subscriber holding the ten segments of their
    /// preview could construct the URL of the eleventh, fetch it with the same signature, and
    /// decrypt it with the key they were correctly given. Scoping the signature to one blob is the
    /// only thing standing between a 60-second preview and the whole song.
    /// </para>
    /// </summary>
    [Test]
    public void GetReadSasQuery_SignsForOneBlobRatherThanTheWholeContainer()
    {
        var provider = Create();

        var granted = provider.GetReadSasQuery("stream/seg-000.ts");
        var withheld = provider.GetReadSasQuery("stream/seg-001.ts");

        Assert.Multiple(() =>
        {
            // sr=b is the blob-scoped resource designator; sr=c would be the container.
            Assert.That(granted, Does.Contain("sr=b"));
            Assert.That(granted, Does.Not.Contain("sr=c"));

            // Different blob, different signature - so the credential for one cannot fetch the other.
            Assert.That(withheld, Is.Not.EqualTo(granted));
        });
    }

    [Test]
    public void GetReadSasQuery_WithANonPositiveLifetime_FallsBackRatherThanSigningADeadSas()
    {
        // A misconfiguration here would otherwise mint a SAS that is already expired, disabling
        // playback in a way that looks like a player bug rather than a settings mistake.
        var query = Create(new HlsOptions { SegmentSasLifetime = TimeSpan.Zero }).GetReadSasQuery(SegmentPath);

        Assert.That(query, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void GetReadSasQuery_WithNoBlobPath_ReturnsNullRatherThanSigningTheContainer()
    {
        // Guards the shape of the mistake this class exists to prevent: an empty path must not
        // degrade into a credential that is broader than the caller asked for.
        Assert.That(Create().GetReadSasQuery(""), Is.Null);
        Assert.That(Create().GetReadSasQuery(null), Is.Null);
    }

    [Test]
    public void GetReadSasQuery_WhenTheClientCannotSign_ReturnsNullRatherThanThrowing()
    {
        var factory = new Mock<IBlobContainerFactory>();

        // No credential, so CanGenerateSasUri is false - what a managed-identity deployment would
        // look like. The manifest endpoint reports it rather than 500ing on every request.
        factory.Setup(f => f.GetStreamingContainer())
            .Returns(new BlobContainerClient(
                new Uri("https://acct.blob.core.windows.net/musicstreaming-test")));

        var provider = new HlsSegmentSasProvider(
            factory.Object,
            Options.Create(new HlsOptions()),
            Mock.Of<ILogger<HlsSegmentSasProvider>>());

        Assert.That(provider.GetReadSasQuery(SegmentPath), Is.Null);
    }
}
