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

    // ---------------------------------------------------------------------
    // Stream lifetime.
    //
    // SKCodec.Create(Stream) wraps the stream in an adapter that OWNS it, so disposing the codec
    // closes the caller's stream. That stayed invisible while every call site passed a throwaway
    // buffer, and became a failed upload the moment the staging path validated the stream it was
    // about to upload: "Cannot access a closed file".
    // ---------------------------------------------------------------------

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Test]
    public void ImageContentMatchesExtension_LeavesTheCallersStreamOpen()
    {
        using var stream = new MemoryStream(CreatePng());

        var matched = MediaFileContentValidator.ImageContentMatchesExtension(stream, "cover.png", out _);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(stream.CanRead, Is.True, "the validator must not dispose a stream it does not own");
        });
    }

    [Test]
    public void ImageContentMatchesExtension_LeavesAFileStreamUsableForAFollowingUpload()
    {
        // The exact shape that failed in production: validate a FileStream, then reposition it to
        // stream the blob out. FileStream is what throws ObjectDisposedException on Position.
        var path = Path.Combine(Path.GetTempPath(), $"cover-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, CreatePng());

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            Assert.That(
                MediaFileContentValidator.ImageContentMatchesExtension(stream, "cover.png", out _),
                Is.True);

            Assert.DoesNotThrow(() => stream.Position = 0);

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.That(copy.Length, Is.GreaterThan(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ImageContentMatchesExtension_LeavesTheBytesIntact()
    {
        var bytes = CreatePng();
        using var stream = new MemoryStream(bytes);

        MediaFileContentValidator.ImageContentMatchesExtension(stream, "cover.png", out _);

        stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        Assert.That(copy.ToArray(), Is.EqualTo(bytes));
    }

    [Test]
    public void ImageContentMatchesExtension_RestoresTheOriginalPosition()
    {
        using var stream = new MemoryStream(CreatePng());
        stream.Position = 3;

        MediaFileContentValidator.ImageContentMatchesExtension(stream, "cover.png", out _);

        Assert.That(stream.Position, Is.EqualTo(3));
    }

    [Test]
    public void ImageContentMatchesExtension_WithCorruptContent_StillLeavesTheStreamOpen()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Multiple(() =>
        {
            Assert.That(MediaFileContentValidator.ImageContentMatchesExtension(stream, "cover.png", out _), Is.False);
            Assert.That(stream.CanRead, Is.True);
        });
    }

    [Test]
    public void ImageContentMatchesExtension_WithAnEmptyStream_IsRejectedWithoutThrowing()
    {
        using var stream = new MemoryStream();

        Assert.Multiple(() =>
        {
            Assert.That(MediaFileContentValidator.ImageContentMatchesExtension(stream, "cover.png", out _), Is.False);
            Assert.That(stream.CanRead, Is.True);
        });
    }
}
