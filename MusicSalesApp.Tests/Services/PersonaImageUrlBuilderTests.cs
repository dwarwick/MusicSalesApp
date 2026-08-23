#nullable enable
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class PersonaImageUrlBuilderTests
{
    private const string Guid32 = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b";
    private static readonly string PersonaImage = $"{Guid32}/{Guid32}-persona.jpg";

    private PersonaImageUrlBuilder _builder = null!;

    [SetUp]
    public void SetUp() => _builder = new PersonaImageUrlBuilder();

    [Test]
    public void BuildProxy_RoutesThroughThePersonaArtEndpoint()
    {
        // Not api/music: persona images live in their own blob container, which is the whole
        // reason they need an endpoint of their own rather than the song media one.
        var url = _builder.BuildProxy(PersonaImage, null, displayWidthCssPx: 40, version: 1);

        Assert.That(url, Does.StartWith("api/persona-art/"));
    }

    [Test]
    public void BuildProxy_CarriesTheVersion()
    {
        // The endpoint marks the response immutable for a year, so the version is the only thing
        // that lets a replaced avatar reach a browser that has already cached the old one.
        var url = _builder.BuildProxy(PersonaImage, "128,320", displayWidthCssPx: 40, version: 9);

        Assert.That(url, Does.EndWith("?v=9"));
    }

    [Test]
    public void BuildProxy_ProducesAStableUrlAcrossCalls()
    {
        // The regression this whole change exists for. The previous SAS-based URL embedded an
        // expiry computed from the current time, so two renders of the same unchanged image
        // produced two different URLs and the browser cache could never hit.
        var first = _builder.BuildProxy(PersonaImage, "128,320", 40, version: 3);
        var second = _builder.BuildProxy(PersonaImage, "128,320", 40, version: 3);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void BuildProxy_PicksTheRenditionAtTwiceTheCssWidth()
    {
        // 40 CSS px on a 2x display needs 80 real pixels, so the 128 rendition is the smallest
        // that still looks sharp.
        var url = _builder.BuildProxy(PersonaImage, "64,128,320", displayWidthCssPx: 40, version: 1);

        Assert.That(url, Does.Contain(".w128.webp"));
    }

    [Test]
    public void BuildProxy_WithNoRecordedWidths_ServesTheMaster()
    {
        // The pre-backfill state, and what the site did before renditions existed.
        var url = _builder.BuildProxy(PersonaImage, null, displayWidthCssPx: 40, version: 0);

        Assert.Multiple(() =>
        {
            Assert.That(url, Does.Contain("persona.jpg"));
            Assert.That(url, Does.Not.Contain(".webp"));
        });
    }

    [Test]
    public void BuildProxy_WhenNoRenditionIsLargeEnough_FallsBackToTheMaster()
    {
        var url = _builder.BuildProxy(PersonaImage, "64", displayWidthCssPx: 200, version: 1);

        Assert.That(url, Does.Not.Contain(".w64.webp"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void BuildProxy_WithNoImage_ReturnsNull(string? blobPath)
        => Assert.That(_builder.BuildProxy(blobPath, "128", 40, 1), Is.Null);

    [TestCase("../../secrets/key.txt")]
    [TestCase("~/etc/passwd")]
    public void BuildProxy_RefusesTraversalPaths(string blobPath)
    {
        // The endpoint takes a caller-supplied path, so a traversal attempt must not even be
        // rendered into a URL. The controller re-checks against the database regardless.
        Assert.That(_builder.BuildProxy(blobPath, null, 40, 1), Is.Null);
    }

    [Test]
    public void BuildProxy_PercentEncodesSegmentsButKeepsSeparators()
    {
        var url = _builder.BuildProxy("folder name/persona image.jpg", null, 40, 1);

        Assert.Multiple(() =>
        {
            Assert.That(url, Does.Contain("folder%20name/persona%20image.jpg"));
            Assert.That(url, Does.StartWith("api/persona-art/folder"));
        });
    }
}
