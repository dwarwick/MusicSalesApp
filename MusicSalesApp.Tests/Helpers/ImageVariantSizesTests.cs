using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class ImageVariantSizesTests
{
    [Test]
    public void TheLaddersAreAscendingAndDistinct()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantSizes.CoverArt, Is.Ordered.Ascending.And.Unique);
            Assert.That(ImageVariantSizes.Persona, Is.Ordered.Ascending.And.Unique);
        });
    }

    [Test]
    public void TheMobileTiersAreBothOnTheCoverArtLadder()
    {
        // MobileSongMapper only emits a rendition URL for a width the generator actually produces.
        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantSizes.CoverArt, Does.Contain(ImageVariantSizes.MobileThumbWidth));
            Assert.That(ImageVariantSizes.CoverArt, Does.Contain(ImageVariantSizes.MobileHeroWidth));
            Assert.That(ImageVariantSizes.MobileThumbWidth, Is.LessThan(ImageVariantSizes.MobileHeroWidth));
        });
    }

    [Test]
    public void ThePersonaThumbWidthIsGeneratedForPersonasToo()
        => Assert.That(ImageVariantSizes.Persona, Does.Contain(ImageVariantSizes.MobileThumbWidth));

    [Test]
    public void ToCsv_ThenParseCsv_RoundTrips()
    {
        var csv = ImageVariantSizes.ToCsv(ImageVariantSizes.CoverArt);

        Assert.Multiple(() =>
        {
            Assert.That(csv, Is.EqualTo("128,320,640,1024"));
            Assert.That(ImageVariantSizes.ParseCsv(csv), Is.EqualTo(ImageVariantSizes.CoverArt));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ToCsv_AndParseCsv_HandleNothingGracefully(string csv)
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantSizes.ParseCsv(csv), Is.Empty);
            Assert.That(ImageVariantSizes.CsvContains(csv, 320), Is.False);
            Assert.That(ImageVariantSizes.SelectAtLeast(csv, 320), Is.Null);
        });
    }

    [Test]
    public void ToCsv_WithNoWidths_IsEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantSizes.ToCsv(null), Is.Empty);
            Assert.That(ImageVariantSizes.ToCsv(Array.Empty<int>()), Is.Empty);
            Assert.That(ImageVariantSizes.ToCsv(new[] { 0, -5 }), Is.Empty);
        });
    }

    [TestCase("128, 320 ,640", new[] { 128, 320, 640 })]
    [TestCase("320,,640,", new[] { 320, 640 })]
    [TestCase("320,abc,640", new[] { 320, 640 })]
    [TestCase("320,320,640", new[] { 320, 640 })]
    [TestCase("-1,0,320", new[] { 320 })]
    [TestCase("garbage", new int[0])]
    public void ParseCsv_SkipsMalformedEntriesRatherThanThrowing(string csv, int[] expected)
    {
        // The stored value is only an optimisation hint. A parse failure must degrade to
        // "serve the master", never break the page.
        Assert.That(ImageVariantSizes.ParseCsv(csv), Is.EqualTo(expected));
    }

    [Test]
    public void CsvContains_MatchesOnlyAnExactWidth()
    {
        const string csv = "128,320,640";

        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantSizes.CsvContains(csv, 320), Is.True);
            Assert.That(ImageVariantSizes.CsvContains(csv, 1024), Is.False);
            Assert.That(ImageVariantSizes.CsvContains(csv, 32), Is.False);
            Assert.That(ImageVariantSizes.CsvContains(csv, 0), Is.False);
        });
    }

    [TestCase(40, 128)]
    [TestCase(128, 128)]
    [TestCase(129, 320)]
    [TestCase(640, 640)]
    public void SelectAtLeast_PicksTheSmallestRenditionThatIsBigEnough(int required, int expected)
        => Assert.That(ImageVariantSizes.SelectAtLeast("128,320,640", required), Is.EqualTo(expected));

    [Test]
    public void SelectAtLeast_WhenEveryRenditionIsTooSmall_IsNull()
    {
        // The caller falls back to the master rather than upscaling a rendition.
        Assert.That(ImageVariantSizes.SelectAtLeast("128,320", 1024), Is.Null);
    }

    [Test]
    public void IsKnownWidth_AcceptsOnlyLadderWidths()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantSizes.IsKnownCoverArtWidth(1024), Is.True);
            Assert.That(ImageVariantSizes.IsKnownCoverArtWidth(200), Is.False);

            // Personas stop at 640; nothing displays one larger than 200 CSS px / 120 DIP.
            Assert.That(ImageVariantSizes.IsKnownPersonaWidth(640), Is.True);
            Assert.That(ImageVariantSizes.IsKnownPersonaWidth(1024), Is.False);
        });
    }
}
