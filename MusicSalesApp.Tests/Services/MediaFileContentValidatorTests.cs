using MusicSalesApp.Services;
using SkiaSharp;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class MediaFileContentValidatorTests
{
    [Test]
    public void ImageContentMatchesExtension_DecodablePng_ReturnsTrue()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());

        var valid = MediaFileContentValidator.ImageContentMatchesExtension(
            stream, "Cover.png", out var detected);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True);
            Assert.That(detected, Is.EqualTo(".png"));
        });
    }

    [Test]
    public void ImageContentMatchesExtension_MismatchedExtension_ReturnsFalse()
    {
        using var bitmap = new SKBitmap(2, 2);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());

        Assert.That(
            MediaFileContentValidator.ImageContentMatchesExtension(stream, "Cover.jpg", out _),
            Is.False);
    }

    [Test]
    public void ImageContentMatchesExtension_CorruptImage_ReturnsFalse()
    {
        using var stream = new MemoryStream([1, 2, 3, 4]);

        Assert.That(
            MediaFileContentValidator.ImageContentMatchesExtension(stream, "Cover.png", out _),
            Is.False);
    }

    [Test]
    public void ImageContentMatchesExtension_TruncatedRecognizablePng_ReturnsFalse()
    {
        using var bitmap = new SKBitmap(32, 32);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)(x * 7), (byte)(y * 7), (byte)(x ^ y)));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var complete = data.ToArray();
        using var stream = new MemoryStream(complete[..(complete.Length / 2)]);

        Assert.That(
            MediaFileContentValidator.ImageContentMatchesExtension(stream, "Cover.png", out _),
            Is.False);
    }
}
