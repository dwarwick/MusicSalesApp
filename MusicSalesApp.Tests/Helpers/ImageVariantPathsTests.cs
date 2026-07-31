using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class ImageVariantPathsTests
{
    private const string GuidName = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b";
    private static readonly string GuidCoverArt = $"{GuidName}/{GuidName}-coverart.jpg";
    private const string LegacyCoverArt = "Night Drive/Night Drive.png";
    private const string PersonaImage = "creator-12/persona-7.png";

    [TestCase("3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b/3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b-coverart.jpg", 320,
        "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b/3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b-coverart.jpg.w320.webp")]
    [TestCase("Night Drive/Night Drive.png", 128, "Night Drive/Night Drive.png.w128.webp")]
    [TestCase("creator-12/persona-7.png", 640, "creator-12/persona-7.png.w640.webp")]
    public void Variant_AppendsTheWidthAndWebpExtensionToTheWholeBasePath(string basePath, int width, string expected)
        => Assert.That(ImageVariantPaths.Variant(basePath, width), Is.EqualTo(expected));

    [TestCase("3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b/3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b-coverart.jpg")]
    [TestCase("3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b/3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b-coverart.png")]
    [TestCase("Night Drive/Night Drive.png")]
    [TestCase("An Album/A Song With.Dots.jpeg")]
    [TestCase("creator-12/persona-7.png")]
    [TestCase("no-folder.jpg")]
    public void TryParseVariant_RoundTripsEveryNamingScheme(string basePath)
    {
        // The base extension has to survive the round trip: it is the whole reason the width marker
        // is appended rather than substituted into the name.
        var variant = ImageVariantPaths.Variant(basePath, 640);

        Assert.That(ImageVariantPaths.TryParseVariant(variant, out var parsedBase, out var width), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(parsedBase, Is.EqualTo(basePath));
            Assert.That(width, Is.EqualTo(640));
        });
    }

    [Test]
    public void TryParseVariant_ReadsTheWidthWhenTheBaseNameAlreadyContainsAWidthMarker()
    {
        // "cover.w2.jpg" is a legal blob name; only the *last* marker before ".webp" is the width.
        var variant = ImageVariantPaths.Variant("art/cover.w2.jpg", 320);

        Assert.That(ImageVariantPaths.TryParseVariant(variant, out var parsedBase, out var width), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(parsedBase, Is.EqualTo("art/cover.w2.jpg"));
            Assert.That(width, Is.EqualTo(320));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("art/cover.jpg")]
    [TestCase("art/cover.webp")]
    [TestCase("art/cover.jpg.w.webp")]
    [TestCase("art/cover.jpg.wabc.webp")]
    [TestCase("art/cover.jpg.w0320.webp")]
    [TestCase("art/cover.jpg.w0.webp")]
    [TestCase("art/cover.jpg.w-320.webp")]
    [TestCase("art/cover.jpg.w99999999.webp")]
    [TestCase(".w320.webp")]
    [TestCase("../secrets/cover.jpg.w320.webp")]
    [TestCase("~/cover.jpg.w320.webp")]
    public void TryParseVariant_RejectsAnythingThatIsNotOneOfOurRenditions(string blobPath)
        => Assert.That(ImageVariantPaths.TryParseVariant(blobPath, out _, out _), Is.False);

    [Test]
    public void TryParseVariant_NormalizesBackslashesAndLeadingSlashes()
    {
        Assert.That(
            ImageVariantPaths.TryParseVariant(@"/Night Drive\Night Drive.png.w320.webp", out var parsedBase, out _),
            Is.True);
        Assert.That(parsedBase, Is.EqualTo(LegacyCoverArt));
    }

    [Test]
    public void IsVariantPath_DistinguishesRenditionsFromMasters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantPaths.IsVariantPath(ImageVariantPaths.Variant(GuidCoverArt, 128)), Is.True);
            Assert.That(ImageVariantPaths.IsVariantPath(GuidCoverArt), Is.False);
            Assert.That(ImageVariantPaths.IsVariantPath(PersonaImage), Is.False);
        });
    }

    [Test]
    public void VariantsFor_BuildsOnePathPerWidthInOrder()
    {
        var paths = ImageVariantPaths.VariantsFor(LegacyCoverArt, ImageVariantSizes.CoverArt);

        Assert.That(paths, Is.EqualTo(new[]
        {
            "Night Drive/Night Drive.png.w128.webp",
            "Night Drive/Night Drive.png.w320.webp",
            "Night Drive/Night Drive.png.w640.webp",
            "Night Drive/Night Drive.png.w1024.webp"
        }));
    }

    [Test]
    public void VariantsFor_WithNoBasePathOrNoWidths_IsEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantPaths.VariantsFor("", ImageVariantSizes.CoverArt), Is.Empty);
            Assert.That(ImageVariantPaths.VariantsFor(LegacyCoverArt, Array.Empty<int>()), Is.Empty);
            Assert.That(ImageVariantPaths.VariantsFor(LegacyCoverArt, null), Is.Empty);
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void Variant_WithoutABasePath_Throws(string basePath)
        => Assert.Throws<ArgumentException>(() => ImageVariantPaths.Variant(basePath, 320));

    [TestCase(0)]
    [TestCase(-1)]
    public void Variant_WithoutAPositiveWidth_Throws(int width)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ImageVariantPaths.Variant(GuidCoverArt, width));

    [Test]
    public void ARenditionOfTheAudioBlobIsStillRecognisedAsARendition()
    {
        // The parser's job is only to split the name. Refusing to serve a rendition whose base is
        // the mp3 rather than the cover art is the media whitelist's job, and it is tested there.
        var forged = ImageVariantPaths.Variant($"{GuidName}/{GuidName}-music.mp3", 320);

        Assert.That(ImageVariantPaths.TryParseVariant(forged, out var parsedBase, out _), Is.True);
        Assert.That(parsedBase, Is.EqualTo($"{GuidName}/{GuidName}-music.mp3"));
    }
}
