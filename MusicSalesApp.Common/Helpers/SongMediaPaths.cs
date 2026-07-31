using System.Globalization;

#nullable enable

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Builds and recognizes the blob paths for a song's media files.
///
/// Every song uploaded from July 2026 onward is identified by a <see cref="Guid"/> that the
/// application mints, and all of its blobs live in a folder named for that GUID:
///
/// <code>
/// {guid}/{guid}-music.mp3               playback (always mp3)
/// {guid}/{guid}-music-original{ext}     the exact audio the creator supplied
/// {guid}/{guid}-coverart{ext}           the cover art actually served
/// {guid}/{guid}-coverart-original{ext}  the exact cover art the creator supplied
/// {guid}/{guid}-fb.png                  the Facebook / Open Graph share image
/// </code>
///
/// Creator filenames therefore never reach storage, which is why uploads accept any filename.
/// The browser-supplied names are retained as data on SongMetadata instead.
///
/// Songs uploaded before that change are named after the creator's filename
/// (<c>Night Drive/Night Drive.mp3</c>) and have no GUID. Those paths still work: nothing
/// reconstructs a stored path, it is always read back from the database. The only place the two
/// schemes must be told apart is the Facebook image, whose path is derived rather than stored —
/// see <see cref="FacebookImageFor"/>.
/// </summary>
public static class SongMediaPaths
{
    /// <summary>
    /// 32 lowercase hex digits, no hyphens. The "N" format matters: with the default "D" format the
    /// GUID itself contains hyphens, which would make the "-music"/"-coverart" suffixes ambiguous
    /// and <see cref="TryGetGuidFromPath"/> unreliable.
    /// </summary>
    private const string GuidFormat = "N";

    private const string PlaybackExtension = ".mp3";

    /// <summary>
    /// Sharing images are JPEG. They were PNG until July 2026: a 1200x630 photographic PNG runs to
    /// 1-2 MB where JPEG at quality 85 is under 150 KB, and the image is composited onto an opaque
    /// black canvas so there is no transparency to preserve.
    /// </summary>
    private const string FacebookExtension = ".jpg";

    /// <summary>The extension sharing images used before the move to JPEG.</summary>
    private const string LegacyFacebookExtension = ".png";

    private const string MusicSuffix = "-music";
    private const string OriginalMusicSuffix = "-music-original";
    private const string CoverArtSuffix = "-coverart";
    private const string OriginalCoverArtSuffix = "-coverart-original";
    private const string FacebookSuffix = "-fb";

    /// <summary>The folder that holds every blob belonging to <paramref name="mediaGuid"/>.</summary>
    public static string Folder(Guid mediaGuid) => Format(mediaGuid);

    /// <summary>The audio actually streamed to listeners. Always mp3.</summary>
    public static string Playback(Guid mediaGuid)
        => Build(mediaGuid, MusicSuffix, PlaybackExtension);

    /// <summary>
    /// The exact audio the creator uploaded, retained privately.
    ///
    /// When the source was already mp3 this returns <see cref="Playback"/> — the upload is both the
    /// original and the playback copy, so it is stored once rather than twice. Callers rely on this:
    /// <c>MusicUploadService</c> compares the two paths to decide whether to write one blob or two.
    /// </summary>
    public static string OriginalAudio(Guid mediaGuid, string extension)
        => IsPlaybackExtension(extension)
            ? Playback(mediaGuid)
            : Build(mediaGuid, OriginalMusicSuffix, extension);

    /// <summary>The cover art served to listeners. Overwritten in place by a crop or a replacement.</summary>
    public static string CoverArt(Guid mediaGuid, string extension)
        => Build(mediaGuid, CoverArtSuffix, extension);

    /// <summary>The exact cover art the creator uploaded, retained privately and never overwritten by a crop.</summary>
    public static string OriginalCoverArt(Guid mediaGuid, string extension)
        => Build(mediaGuid, OriginalCoverArtSuffix, extension);

    /// <summary>The 1200x630 Open Graph share image, generated on demand from the cover art.</summary>
    public static string FacebookImage(Guid mediaGuid)
        => Build(mediaGuid, FacebookSuffix, FacebookExtension);

    /// <summary>
    /// Resolves the Facebook share image for a cover art path under either naming scheme.
    ///
    /// GUID-scheme art resolves to the song's fixed <c>{guid}-fb.png</c>; legacy art keeps the
    /// original <c>{folder}/{name}_fb.png</c> rule so share images already in storage stay reachable.
    /// </summary>
    public static string FacebookImageFor(string? coverArtBlobPath)
    {
        if (string.IsNullOrWhiteSpace(coverArtBlobPath))
            return string.Empty;

        if (TryGetGuidFromPath(coverArtBlobPath, out var mediaGuid))
            return FacebookImage(mediaGuid);

        return LegacyFacebookImageFor(coverArtBlobPath, FacebookExtension);
    }

    /// <summary>
    /// Every path a song's sharing image may occupy, current name first.
    ///
    /// <para>
    /// Sharing images moved from PNG to JPEG. The superseded name is returned alongside the current
    /// one so invalidation can clear both - otherwise the stale PNG would keep being served by any
    /// crawler that had already cached its URL - and so the image-variant backfill can sweep the
    /// orphaned PNGs up in the same pass.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FacebookImageCandidatesFor(string? coverArtBlobPath)
    {
        if (string.IsNullOrWhiteSpace(coverArtBlobPath))
            return Array.Empty<string>();

        var current = FacebookImageFor(coverArtBlobPath);

        var legacy = TryGetGuidFromPath(coverArtBlobPath, out var mediaGuid)
            ? Build(mediaGuid, FacebookSuffix, LegacyFacebookExtension)
            : LegacyFacebookImageFor(coverArtBlobPath, LegacyFacebookExtension);

        return string.Equals(current, legacy, StringComparison.OrdinalIgnoreCase)
            ? new[] { current }
            : new[] { current, legacy };
    }

    private static string LegacyFacebookImageFor(string coverArtBlobPath, string extension)
    {
        var normalized = Normalize(coverArtBlobPath);
        var directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
        var fileName = $"{Path.GetFileNameWithoutExtension(normalized)}_fb{extension}";
        return string.IsNullOrEmpty(directory) ? fileName : $"{directory}/{fileName}";
    }

    /// <summary>
    /// Resolves where a song's cover art should be written when it is replaced or cropped.
    ///
    /// GUID-scheme songs always write to the same fixed path. Legacy songs keep the historical
    /// derivation - reuse the existing art path with the new extension, else sit beside the
    /// playback file, else fall back to the song title.
    /// </summary>
    public static string ResolveCoverArtTarget(
        Guid? mediaGuid,
        string? existingCoverArtPath,
        string? playbackBlobPath,
        string? songTitle,
        string extension)
    {
        if (mediaGuid.HasValue)
            return CoverArt(mediaGuid.Value, extension);

        var normalizedExtension = NormalizeExtension(extension);

        if (!string.IsNullOrWhiteSpace(existingCoverArtPath))
            return Path.ChangeExtension(Normalize(existingCoverArtPath), normalizedExtension);

        if (!string.IsNullOrWhiteSpace(playbackBlobPath))
        {
            var normalizedPlayback = Normalize(playbackBlobPath);
            var directory = Path.GetDirectoryName(normalizedPlayback)?.Replace('\\', '/') ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(normalizedPlayback);
            return string.IsNullOrEmpty(directory)
                ? $"{baseName}{normalizedExtension}"
                : $"{directory}/{baseName}{normalizedExtension}";
        }

        return $"{songTitle}{normalizedExtension}";
    }

    /// <summary>
    /// Reads the media GUID out of a blob path, which is its first path segment.
    /// Returns <see langword="false"/> for legacy name-based paths.
    /// </summary>
    public static bool TryGetGuidFromPath(string? blobPath, out Guid mediaGuid)
    {
        mediaGuid = Guid.Empty;
        if (string.IsNullOrWhiteSpace(blobPath))
            return false;

        var firstSegment = Normalize(blobPath).Split('/', 2)[0];
        return Guid.TryParseExact(firstSegment, GuidFormat, out mediaGuid);
    }

    /// <summary>Whether <paramref name="blobPath"/> belongs to the GUID naming scheme.</summary>
    public static bool IsGuidScheme(string? blobPath) => TryGetGuidFromPath(blobPath, out _);

    private static string Build(Guid mediaGuid, string suffix, string extension)
    {
        var name = Format(mediaGuid);
        return $"{name}/{name}{suffix}{NormalizeExtension(extension)}";
    }

    private static string Format(Guid mediaGuid)
        => mediaGuid.ToString(GuidFormat, CultureInfo.InvariantCulture);

    private static string Normalize(string blobPath)
        => blobPath.Replace('\\', '/').TrimStart('/');

    private static bool IsPlaybackExtension(string? extension)
        => string.Equals(NormalizeExtension(extension), PlaybackExtension, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var trimmed = extension.Trim().ToLowerInvariant();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }
}
