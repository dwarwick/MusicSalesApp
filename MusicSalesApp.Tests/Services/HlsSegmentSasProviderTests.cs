using System;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The credential every segment URL carries, now that the streaming container is private.
/// </summary>
[TestFixture]
public class HlsSegmentSasProviderTests
{
    /// <summary>A key-based connection string, so the client can actually sign. Not a real account.</summary>
    private const string ConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=devstoreaccount1;"
        + "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
        + "EndpointSuffix=core.windows.net";

    private static HlsSegmentSasProvider Create(HlsOptions options = null, IMemoryCache cache = null)
    {
        var factory = new Mock<IBlobContainerFactory>();
        factory.Setup(f => f.GetStreamingContainer())
            .Returns(new BlobContainerClient(ConnectionString, "musicstreaming-test"));

        return new HlsSegmentSasProvider(
            factory.Object,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options ?? new HlsOptions()),
            Mock.Of<ILogger<HlsSegmentSasProvider>>());
    }

    [Test]
    public void GetReadSasQuery_ProducesASignedReadOnlyQuery()
    {
        var query = Create().GetReadSasQuery();

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

    [Test]
    public void GetReadSasQuery_IsCached()
    {
        var provider = Create();

        var first = provider.GetReadSasQuery();
        var second = provider.GetReadSasQuery();

        // Signing is cheap, but the manifest endpoint is on the playback path and re-signing per
        // request would be pure waste.
        Assert.That(second, Is.EqualTo(first));
    }

    /// <summary>
    /// The cache must expire well before the SAS does.
    ///
    /// <para>
    /// Caching for the SAS's full lifetime would eventually hand out one with seconds left on it,
    /// and playback would fail part-way through a song for no visible reason. An eighth is arbitrary
    /// but generous: every SAS served still has at least seven eighths of its life left.
    /// </para>
    /// </summary>
    [Test]
    public void GetReadSasQuery_IsCachedForFarLessThanTheSasLifetime()
    {
        var cache = new Mock<IMemoryCache>();
        var entry = new Mock<ICacheEntry>();
        entry.SetupAllProperties();

        object ignored;
        cache.Setup(c => c.TryGetValue(It.IsAny<object>(), out ignored)).Returns(false);
        cache.Setup(c => c.CreateEntry(It.IsAny<object>())).Returns(entry.Object);

        var lifetime = TimeSpan.FromHours(8);
        Create(new HlsOptions { SegmentSasLifetime = lifetime }, cache.Object).GetReadSasQuery();

        Assert.That(entry.Object.AbsoluteExpirationRelativeToNow, Is.LessThan(lifetime / 2));
    }

    [Test]
    public void GetReadSasQuery_WithANonPositiveLifetime_FallsBackRatherThanSigningADeadSas()
    {
        // A misconfiguration here would otherwise mint a SAS that is already expired, disabling
        // playback in a way that looks like a player bug rather than a settings mistake.
        var query = Create(new HlsOptions { SegmentSasLifetime = TimeSpan.Zero }).GetReadSasQuery();

        Assert.That(query, Is.Not.Null.And.Not.Empty);
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
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new HlsOptions()),
            Mock.Of<ILogger<HlsSegmentSasProvider>>());

        Assert.That(provider.GetReadSasQuery(), Is.Null);
    }
}
