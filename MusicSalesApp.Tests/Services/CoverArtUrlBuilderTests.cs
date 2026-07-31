using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class CoverArtUrlBuilderTests
{
    private const string Guid32 = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b";
    private static readonly string CoverArt = $"{Guid32}/{Guid32}-coverart.jpg";

    private Mock<IAzureStorageService> _storage = null!;
    private CoverArtUrlBuilder _builder = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new Mock<IAzureStorageService>();
        _storage.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns((string path, TimeSpan _) => new Uri($"https://blob.test/{path}?sig=abc"));

        _builder = new CoverArtUrlBuilder(_storage.Object, Mock.Of<ILogger<CoverArtUrlBuilder>>());
    }

    [Test]
    public void BuildProxy_ProducesOneUrlPerRecordedWidth()
    {
        var source = _builder.BuildProxy(CoverArt, "128,320,640", version: 2);

        Assert.Multiple(() =>
        {
            Assert.That(source.HasImage, Is.True);
            Assert.That(source.Variants.Select(v => v.Width), Is.EqualTo(new[] { 128, 320, 640 }));
            Assert.That(source.Variants[1].Url, Does.Contain($"{CoverArt}.w320.webp".Replace("/", "%2F")).Or
                .Contain("coverart.jpg.w320.webp"));
        });
    }

    [Test]
    public void BuildProxy_CarriesTheVersionOnEveryUrl()
    {
        // Cover art under the GUID scheme has a fixed path a re-crop overwrites in place, and the
        // media endpoint marks it immutable for a year. Without the version, a recropped image would
        // stay stale in every browser that had already seen it.
        var source = _builder.BuildProxy(CoverArt, "128,320", version: 7);

        Assert.That(source.BaseUrl, Does.EndWith("?v=7"));
        Assert.That(source.Variants.Select(v => v.Url), Is.All.EndsWith("?v=7"));
    }

    [Test]
    public void BuildProxy_RoutesThroughTheMediaEndpoint()
        => Assert.That(_builder.BuildProxy(CoverArt, null, 1).BaseUrl, Does.StartWith("api/music/"));

    [Test]
    public void BuildProxy_WithNoRecordedWidths_StillServesTheMaster()
    {
        // The pre-backfill state. The component renders a plain img tag from this.
        var source = _builder.BuildProxy(CoverArt, null, version: 0);

        Assert.Multiple(() =>
        {
            Assert.That(source.HasImage, Is.True);
            Assert.That(source.HasVariants, Is.False);
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void BuildProxy_WithoutABlobPath_IsNone(string blobPath)
        => Assert.That(_builder.BuildProxy(blobPath, "128,320", 1), Is.EqualTo(CoverArtSource.None));

    [TestCase("../secrets/key.jpg")]
    [TestCase("~/key.jpg")]
    public void BuildProxy_RefusesTraversalPaths(string blobPath)
        => Assert.That(_builder.BuildProxy(blobPath, null, 1), Is.EqualTo(CoverArtSource.None));

    [Test]
    public void BuildProxy_EncodesEachSegmentButKeepsTheSeparators()
    {
        var source = _builder.BuildProxy("Night Drive/Night Drive.png", null, 1);

        Assert.That(source.BaseUrl, Is.EqualTo("api/music/Night%20Drive/Night%20Drive.png?v=1"));
    }

    [Test]
    public void BuildSas_MintsOneSignaturePerRendition()
    {
        var source = _builder.BuildSas(CoverArt, "128,320,640", version: 1, TimeSpan.FromHours(1));

        Assert.That(source.Variants, Has.Count.EqualTo(3));
        _storage.Verify(s => s.GetReadSasUri(It.IsAny<string>(), TimeSpan.FromHours(1)), Times.Exactly(4),
            "one for the master plus one per rendition");
    }

    [Test]
    public void BuildSas_PointsAtTheRenditionBlobPaths()
    {
        var source = _builder.BuildSas(CoverArt, "320", version: 1, TimeSpan.FromHours(1));

        Assert.That(source.Variants.Single().Url, Does.Contain($"{CoverArt}.w320.webp"));
    }

    [Test]
    public void BuildSas_WhenSigningFails_DegradesToNoImageRatherThanThrowing()
    {
        _storage.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Throws(new InvalidOperationException("cannot generate SAS"));

        Assert.That(
            _builder.BuildSas(CoverArt, "128", 1, TimeSpan.FromHours(1)),
            Is.EqualTo(CoverArtSource.None));
    }

    [Test]
    public void BuildSas_WhenOneRenditionCannotBeSigned_KeepsTheRest()
    {
        var variantPath = $"{CoverArt}.w320.webp";
        _storage.Setup(s => s.GetReadSasUri(variantPath, It.IsAny<TimeSpan>()))
            .Throws(new InvalidOperationException("nope"));

        var source = _builder.BuildSas(CoverArt, "128,320,640", 1, TimeSpan.FromHours(1));

        Assert.That(source.Variants.Select(v => v.Width), Is.EqualTo(new[] { 128, 640 }));
    }

    [Test]
    public void MalformedWidthSets_DegradeToServingTheMaster()
    {
        var source = _builder.BuildProxy(CoverArt, "garbage,,-4", version: 1);

        Assert.Multiple(() =>
        {
            Assert.That(source.HasImage, Is.True);
            Assert.That(source.HasVariants, Is.False);
        });
    }
}
