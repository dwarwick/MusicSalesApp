#nullable enable

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Identifies an audio container from its first bytes.
///
/// <para>
/// Cheap enough to run on a request thread - it reads 64 bytes and compares magic numbers, with no
/// decoding and no FFmpeg. That is what makes it the right gate on the upload path: the web app can
/// reject an obviously-wrong file instantly, while the real decode happens later in the Azure
/// Function.
/// </para>
///
/// <para>
/// Lives in Common because both sides need it - the web app sniffs uploads before staging them, and
/// the Function sniffs its own FFmpeg output before spending a second decode proving it. It is
/// deliberately dependency-free so it can sit alongside the constants the MAUI app also references.
/// </para>
/// </summary>
public static class AudioContainerSniffer
{
    private static readonly byte[] AsfHeader =
        { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };

    /// <summary>
    /// Whether the stream's actual container matches what <paramref name="fileName"/> claims.
    /// </summary>
    public static bool ContentMatchesExtension(Stream stream, string fileName, out string detectedExtension)
    {
        detectedExtension = DetectExtension(stream) ?? string.Empty;
        if (string.IsNullOrEmpty(detectedExtension))
        {
            return false;
        }

        var expectedExtension = Path.GetExtension(fileName).ToLowerInvariant();
        return string.Equals(expectedExtension, detectedExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The container this stream actually holds, or null if it is not one this app supports.
    /// Restores the stream position, so the caller can go on to read it.
    /// </summary>
    public static string? DetectExtension(Stream stream)
    {
        if (stream == null || !stream.CanRead)
        {
            return null;
        }

        var originalPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            Span<byte> header = stackalloc byte[64];
            var bytesRead = stream.Read(header);
            var data = header[..bytesRead];

            // Either an ID3 tag, or a raw MPEG audio frame sync (11 set bits) with a valid layer.
            if (StartsWith(data, "ID3"u8) ||
                (bytesRead >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0 && (data[1] & 0x06) != 0))
                return ".mp3";

            // RF64 is the >4 GB variant of RIFF/WAVE and is otherwise identical here.
            if (bytesRead >= 12 &&
                (StartsWith(data, "RIFF"u8) || StartsWith(data, "RF64"u8)) &&
                data[8..12].SequenceEqual("WAVE"u8))
                return ".wav";

            if (StartsWith(data, "fLaC"u8))
                return ".flac";

            if (StartsWith(data, "OggS"u8))
                return ".ogg";

            // ISO base media (MP4/M4A): the 'ftyp' box follows a 4-byte size.
            if (bytesRead >= 12 && data[4..8].SequenceEqual("ftyp"u8))
                return ".m4a";

            // ADTS sync word - checked after MP3 because the two masks overlap.
            if (bytesRead >= 2 && data[0] == 0xFF && (data[1] & 0xF6) == 0xF0)
                return ".aac";

            if (bytesRead >= AsfHeader.Length && data[..AsfHeader.Length].SequenceEqual(AsfHeader))
                return ".wma";

            return null;
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }
        }
    }

    private static bool StartsWith(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prefix)
        => source.Length >= prefix.Length && source[..prefix.Length].SequenceEqual(prefix);
}
