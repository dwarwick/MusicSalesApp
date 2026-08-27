using System.Globalization;

#nullable enable

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Builds and recognizes the blob paths inside the encrypted-HLS streaming container.
///
/// <para>
/// Deliberately separate from <see cref="SongMediaPaths"/>, which documents the <em>media</em>
/// container and is built around a song's <c>MediaGuid</c>. These paths live in a different
/// container, under a different identifier, and follow none of that class's rules — folding them in
/// would make its class-level documentation false.
/// </para>
///
/// <para>
/// The identifier is a song's <c>HlsStreamId</c>, not its <c>MediaGuid</c>, for two reasons.
/// Pre-July-2026 songs have no media GUID at all, and minting one for them would change what
/// <see cref="SongMediaPaths.FacebookImageFor"/> resolves to and break share images already in
/// circulation. And repackaging mints a <b>new</b> <c>HlsStreamId</c> rather than overwriting the
/// old folder, which is what makes a package immutable at its path — so nothing downstream needs a
/// version query parameter to defeat caching, and a half-written folder from a failed run can never
/// be mistaken for the live one.
/// </para>
///
/// <para>
/// The container is private, like every other one in this product; segment URLs carry a container
/// read SAS the API stamps on per request. It was going to be public - the design called for stable
/// credential-free segment URLs - but both storage accounts set <c>allowBlobPublicAccess: false</c>,
/// and the premium account holds every song master and the Data Protection key rings for Production
/// as well as Test, so the guardrail was worth more.
/// </para>
/// </summary>
public static class HlsPackagePaths
{
    /// <summary>
    /// 32 lowercase hex digits, no hyphens — the same "N" convention
    /// <see cref="SongMediaPaths"/> uses, so folder names look alike across containers.
    /// </summary>
    private const string StreamIdFormat = "N";

    private const string SegmentPrefix = "seg-";
    private const string SegmentExtension = ".ts";

    /// <summary>The manifest's fixed leaf name. One rendition, so there is no master playlist.</summary>
    public const string ManifestFileName = "index.m3u8";

    /// <summary>
    /// The key URI FFmpeg bakes into a stored manifest, and that the API substitutes per request.
    ///
    /// <para>
    /// A placeholder rather than a real URL because the real one carries a token that lives about a
    /// minute. Writing that into storage would both persist a credential and pin the manifest to one
    /// listener at one moment, when the stored manifest has to be reusable and the token must not be.
    /// </para>
    ///
    /// <para>
    /// Lives here rather than in either project because the Function writes it and the web app
    /// matches on it — the same reason every other name in <c>MediaProcessingConstants</c> is
    /// shared. A drift between two private copies would surface as a placeholder reaching a player,
    /// with no compiler error and no exception on either side.
    /// </para>
    /// </summary>
    public const string KeyUriPlaceholder = "streamtunes:key";

    /// <summary>
    /// The FFmpeg <c>-hls_segment_filename</c> pattern. <c>%03d</c> is a <em>minimum</em> width, so
    /// a track long enough to need a four-digit index simply gets one — which is why
    /// <see cref="Segment"/> formats with <c>D3</c> rather than padding to a fixed length.
    /// </summary>
    public const string SegmentFilePattern = SegmentPrefix + "%03d" + SegmentExtension;

    /// <summary>
    /// The manifest content type. Apple's <c>application/vnd.apple.mpegurl</c> rather than the
    /// older <c>application/x-mpegURL</c>; both work, this one is the registered type.
    /// </summary>
    public const string ManifestContentType = "application/vnd.apple.mpegurl";

    /// <summary>MPEG-TS segment content type.</summary>
    public const string SegmentContentType = "video/mp2t";

    /// <summary>The folder holding one package's manifest and segments.</summary>
    public static string Folder(Guid hlsStreamId)
        => hlsStreamId.ToString(StreamIdFormat, CultureInfo.InvariantCulture);

    /// <summary>The manifest, as stored — its <c>#EXT-X-KEY</c> URI is still a placeholder.</summary>
    public static string Manifest(Guid hlsStreamId)
        => $"{Folder(hlsStreamId)}/{ManifestFileName}";

    /// <summary>One encrypted segment, numbered as FFmpeg numbered it.</summary>
    public static string Segment(Guid hlsStreamId, int index)
        => $"{Folder(hlsStreamId)}/{SegmentFileName(index)}";

    /// <summary>The leaf name of one segment, matching <see cref="SegmentFilePattern"/>.</summary>
    public static string SegmentFileName(int index)
        => SegmentPrefix + index.ToString("D3", CultureInfo.InvariantCulture) + SegmentExtension;

    /// <summary>
    /// True when <paramref name="fileName"/> is a segment leaf name. Used when uploading FFmpeg's
    /// output directory, so nothing unexpected it left behind gets published.
    /// </summary>
    public static bool IsSegmentFileName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
            && fileName.StartsWith(SegmentPrefix, StringComparison.Ordinal)
            && fileName.EndsWith(SegmentExtension, StringComparison.OrdinalIgnoreCase);
}
