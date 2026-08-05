using MusicSalesApp.Common.Helpers;
using SkiaSharp;

#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// Header-level checks on uploaded media.
///
/// <para>
/// The audio half moved to <see cref="AudioContainerSniffer"/> in the Common project when FFmpeg
/// processing moved to Azure Functions - the Function needs the same sniff, and it is pure byte
/// inspection with no dependencies. The image half stays here because it decodes through SkiaSharp,
/// which has no business in a library the MAUI app also references. These forwarders exist so the
/// web app's call sites did not have to change.
/// </para>
/// </summary>
public static class MediaFileContentValidator
{
    /// <inheritdoc cref="AudioContainerSniffer.ContentMatchesExtension"/>
    public static bool AudioContentMatchesExtension(Stream stream, string fileName, out string detectedExtension)
        => AudioContainerSniffer.ContentMatchesExtension(stream, fileName, out detectedExtension);

    /// <summary>
    /// Whether the stream decodes as an image matching <paramref name="fileName"/>'s extension.
    ///
    /// <para>
    /// <b>Leaves the caller's stream open and repositioned.</b> That is not free:
    /// <c>SKCodec.Create(Stream)</c> wraps the stream in an adapter that <em>owns</em> it, so
    /// disposing the codec closes the caller's stream. Every original call site handed in a
    /// throwaway buffer, which hid it; the upload path now validates the stream it is about to
    /// upload, and closing it there produced "Cannot access a closed file" at the next read.
    /// Reading through <see cref="SKData"/> sidesteps the ownership entirely.
    /// </para>
    /// </summary>
    public static bool ImageContentMatchesExtension(Stream stream, string fileName, out string detectedExtension)
    {
        detectedExtension = string.Empty;
        if (stream == null || !stream.CanRead)
            return false;

        var originalPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek)
                stream.Position = 0;

            // SKData.Create reads the bytes out; it does not take ownership of the stream. Bounded
            // by the cover-art upload cap, so the transient copy is small.
            using var data = SKData.Create(stream);
            if (data == null)
                return false;

            using var codec = SKCodec.Create(data);
            if (codec == null)
                return false;

            if (codec.Info.Width <= 0 || codec.Info.Height <= 0)
                return false;

            using var bitmap = new SKBitmap(codec.Info);
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                return false;

            detectedExtension = codec.EncodedFormat switch
            {
                SKEncodedImageFormat.Jpeg => ".jpg",
                SKEncodedImageFormat.Png => ".png",
                _ => string.Empty
            };

            var expectedExtension = Path.GetExtension(fileName).ToLowerInvariant();
            return detectedExtension switch
            {
                ".jpg" => expectedExtension is ".jpg" or ".jpeg",
                ".png" => expectedExtension == ".png",
                _ => false
            };
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = originalPosition;
        }
    }

    /// <inheritdoc cref="AudioContainerSniffer.DetectExtension"/>
    public static string? DetectAudioExtension(Stream stream)
        => AudioContainerSniffer.DetectExtension(stream);
}
