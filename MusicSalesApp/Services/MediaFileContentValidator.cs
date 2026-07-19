using MusicSalesApp.Common.Helpers;
using SkiaSharp;

#nullable enable

namespace MusicSalesApp.Services;

public static class MediaFileContentValidator
{
    private static readonly byte[] AsfHeader =
        { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };

    public static bool AudioContentMatchesExtension(Stream stream, string fileName, out string detectedExtension)
    {
        detectedExtension = DetectAudioExtension(stream) ?? string.Empty;
        if (string.IsNullOrEmpty(detectedExtension))
            return false;

        var expectedExtension = Path.GetExtension(fileName).ToLowerInvariant();
        return string.Equals(expectedExtension, detectedExtension, StringComparison.OrdinalIgnoreCase);
    }

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

            using var codec = SKCodec.Create(stream);
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

    public static string? DetectAudioExtension(Stream stream)
    {
        if (stream == null || !stream.CanRead)
            return null;

        var originalPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek)
                stream.Position = 0;

            Span<byte> header = stackalloc byte[64];
            var bytesRead = stream.Read(header);
            var data = header[..bytesRead];

            if (StartsWith(data, "ID3"u8) ||
                (bytesRead >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0 && (data[1] & 0x06) != 0))
                return ".mp3";

            if (bytesRead >= 12 &&
                (StartsWith(data, "RIFF"u8) || StartsWith(data, "RF64"u8)) &&
                data[8..12].SequenceEqual("WAVE"u8))
                return ".wav";

            if (StartsWith(data, "fLaC"u8))
                return ".flac";

            if (StartsWith(data, "OggS"u8))
                return ".ogg";

            if (bytesRead >= 12 && data[4..8].SequenceEqual("ftyp"u8))
                return ".m4a";

            if (bytesRead >= 2 && data[0] == 0xFF && (data[1] & 0xF6) == 0xF0)
                return ".aac";

            if (bytesRead >= AsfHeader.Length && data[..AsfHeader.Length].SequenceEqual(AsfHeader))
                return ".wma";

            return null;
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = originalPosition;
        }
    }

    private static bool StartsWith(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prefix)
        => source.Length >= prefix.Length && source[..prefix.Length].SequenceEqual(prefix);
}
