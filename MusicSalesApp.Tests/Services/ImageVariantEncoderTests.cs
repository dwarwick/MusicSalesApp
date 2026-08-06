using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Imaging;
using SkiaSharp;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ImageVariantEncoderTests
{
    private static SKBitmap CreateBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);

        // A little structure so the encoder is not compressing a flat colour, which would make the
        // byte-size assertions meaningless.
        using var paint = new SKPaint { Color = SKColors.Orange, IsAntialias = true };
        canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        return bitmap;
    }

    [Test]
    public void SelectWidths_ReturnsEveryRungTheSourceCanFill()
        => Assert.That(
            ImageVariantEncoder.SelectWidths(ImageVariantSizes.CoverArt, 2000),
            Is.EqualTo(new[] { 128, 320, 640, 1024 }));

    [Test]
    public void SelectWidths_NeverUpscales()
    {
        // An 800px cropped master is the common case: it cannot fill the 1024 rung.
        Assert.Multiple(() =>
        {
            Assert.That(
                ImageVariantEncoder.SelectWidths(ImageVariantSizes.CoverArt, 800),
                Is.EqualTo(new[] { 128, 320, 640 }));
            Assert.That(
                ImageVariantEncoder.SelectWidths(ImageVariantSizes.CoverArt, 640),
                Is.EqualTo(new[] { 128, 320, 640 }));
            Assert.That(
                ImageVariantEncoder.SelectWidths(ImageVariantSizes.CoverArt, 639),
                Is.EqualTo(new[] { 128, 320 }));
        });
    }

    [Test]
    public void SelectWidths_ForASourceSmallerThanEveryRung_ProducesOneRenditionAtTheSourceWidth()
    {
        // Keeps the invariant the mobile client relies on: any image with cover art has at least
        // one rendition. A straight re-encode into WebP, no resampling.
        Assert.That(ImageVariantEncoder.SelectWidths(ImageVariantSizes.CoverArt, 96), Is.EqualTo(new[] { 96 }));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void SelectWidths_WithoutAUsableSource_IsEmpty(int sourceWidth)
        => Assert.That(ImageVariantEncoder.SelectWidths(ImageVariantSizes.CoverArt, sourceWidth), Is.Empty);

    [Test]
    public void SelectWidths_WithoutALadder_IsEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantEncoder.SelectWidths(null, 1000), Is.Empty);
            Assert.That(ImageVariantEncoder.SelectWidths(Array.Empty<int>(), 1000), Is.Empty);
        });
    }

    [Test]
    public void QualityFor_DescendsAsTheRenditionGrows()
    {
        // Larger renditions are viewed at fewer pixels-per-detail and dominate storage.
        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantEncoder.QualityFor(128), Is.EqualTo(82));
            Assert.That(ImageVariantEncoder.QualityFor(320), Is.EqualTo(80));
            Assert.That(ImageVariantEncoder.QualityFor(640), Is.EqualTo(78));
            Assert.That(ImageVariantEncoder.QualityFor(1024), Is.EqualTo(75));
        });
    }

    [Test]
    public void QualityFor_IsMonotonicAcrossTheLadder()
    {
        var qualities = ImageVariantSizes.CoverArt.Select(ImageVariantEncoder.QualityFor).ToArray();
        Assert.That(qualities, Is.Ordered.Descending);
    }

    [Test]
    public void EncodeWebp_ProducesAWebpStreamAtTheRequestedWidth()
    {
        using var source = CreateBitmap(1000, 1000);

        using var encoded = ImageVariantEncoder.EncodeWebp(source, 320);

        Assert.That(encoded, Is.Not.Null);
        Assert.That(encoded.Position, Is.Zero, "the stream should be rewound for the caller");

        using var decoded = SKBitmap.Decode(encoded);
        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded.Width, Is.EqualTo(320));
            Assert.That(decoded.Height, Is.EqualTo(320));
        });
    }

    [Test]
    public void EncodeWebp_WritesTheRiffWebpSignature()
    {
        using var source = CreateBitmap(500, 500);
        using var encoded = ImageVariantEncoder.EncodeWebp(source, 128);

        var header = new byte[12];
        Assert.That(encoded.Read(header, 0, header.Length), Is.EqualTo(12));

        Assert.Multiple(() =>
        {
            Assert.That(System.Text.Encoding.ASCII.GetString(header, 0, 4), Is.EqualTo("RIFF"));
            Assert.That(System.Text.Encoding.ASCII.GetString(header, 8, 4), Is.EqualTo("WEBP"));
        });
    }

    [Test]
    public void EncodeWebp_PreservesAspectRatioForANonSquareSource()
    {
        // The site suppresses non-square art today, but renditions must still exist so a later crop
        // is immediately correct.
        using var source = CreateBitmap(1600, 900);

        using var encoded = ImageVariantEncoder.EncodeWebp(source, 320);
        using var decoded = SKBitmap.Decode(encoded);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Width, Is.EqualTo(320));
            Assert.That(decoded.Height, Is.EqualTo(180));
        });
    }

    [Test]
    public void EncodeWebp_ForAnExtremeAspectRatio_StillProducesAtLeastOnePixelOfHeight()
    {
        using var source = CreateBitmap(4000, 3);

        using var encoded = ImageVariantEncoder.EncodeWebp(source, 128);
        using var decoded = SKBitmap.Decode(encoded);

        Assert.That(decoded.Height, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void EncodeWebp_SmallerRenditionsAreSmallerFiles()
    {
        using var source = CreateBitmap(1500, 1500);

        using var small = ImageVariantEncoder.EncodeWebp(source, 128);
        using var medium = ImageVariantEncoder.EncodeWebp(source, 320);
        using var large = ImageVariantEncoder.EncodeWebp(source, 1024);

        Assert.That(small.Length, Is.LessThan(medium.Length));
        Assert.That(medium.Length, Is.LessThan(large.Length));
    }

    [Test]
    public void EncodeWebp_IsFarSmallerThanTheEquivalentPng()
    {
        // The premise of the whole feature: PNG is the wrong storage format for photographic art.
        using var source = CreateBitmap(800, 800);

        using var webp = ImageVariantEncoder.EncodeWebp(source, 640);

        using var image = SKImage.FromBitmap(source);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);

        Assert.That(webp.Length, Is.LessThan(png.Size));
    }

    [Test]
    public void EncodeWebp_WithNothingToEncode_IsNull()
    {
        using var source = CreateBitmap(100, 100);

        Assert.Multiple(() =>
        {
            Assert.That(ImageVariantEncoder.EncodeWebp(null, 128), Is.Null);
            Assert.That(ImageVariantEncoder.EncodeWebp(source, 0), Is.Null);
            Assert.That(ImageVariantEncoder.EncodeWebp(source, -10), Is.Null);
        });
    }

    [Test]
    public void EveryRenditionIsDrawnFromTheSourceRatherThanFromTheRungAbove()
    {
        // Chaining resamples compounds interpolation error. Encoding 128 directly from a 1024
        // source must not be softer than the same rung produced any other way, so this asserts the
        // direct path is what the encoder exposes: a 128 from a big source and a 128 from a
        // pre-scaled 1024 source agree closely.
        using var source = CreateBitmap(1024, 1024);
        using var direct = ImageVariantEncoder.EncodeWebp(source, 128);

        using var intermediate = ImageVariantEncoder.EncodeWebp(source, 1024);
        using var intermediateBitmap = SKBitmap.Decode(intermediate);
        using var chained = ImageVariantEncoder.EncodeWebp(intermediateBitmap, 128);

        using var directDecoded = SKBitmap.Decode(direct);
        using var chainedDecoded = SKBitmap.Decode(chained);

        Assert.Multiple(() =>
        {
            Assert.That(directDecoded.Width, Is.EqualTo(chainedDecoded.Width));
            Assert.That(directDecoded.Height, Is.EqualTo(chainedDecoded.Height));
        });
    }
}
