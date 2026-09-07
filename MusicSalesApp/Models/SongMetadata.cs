using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSalesApp.Models;

/// <summary>
/// Stores metadata for songs and albums that was previously stored in Azure Blob index tags
/// </summary>
public class SongMetadata
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Identifies this song's media in blob storage. All of its blobs live in a folder named for
    /// this GUID - see <see cref="Common.Helpers.SongMediaPaths"/>.
    ///
    /// Null for songs uploaded before the GUID scheme, whose blobs are named after the creator's
    /// original filename. Those paths are still read straight from the columns below; only derived
    /// paths (the Facebook image, a crop target) need to branch on this being null.
    /// </summary>
    public Guid? MediaGuid { get; set; }

    /// <summary>
    /// Full path to the blob file (folder/filename) - DEPRECATED: Use Mp3BlobPath or ImageBlobPath instead
    /// </summary>
    [MaxLength(500)]
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the MP3 blob file (folder/filename)
    /// </summary>
    [MaxLength(500)]
    public string Mp3BlobPath { get; set; }

    /// <summary>
    /// Full path to the exact validated audio file supplied by the creator.
    /// For original MP3 uploads this is the same path as <see cref="Mp3BlobPath"/>.
    /// </summary>
    [MaxLength(500)]
    public string OriginalAudioBlobPath { get; set; }

    /// <summary>
    /// Browser-supplied filename for the retained original audio.
    /// </summary>
    [MaxLength(255)]
    public string OriginalAudioFileName { get; set; }

    public long? OriginalAudioFileSize { get; set; }

    [MaxLength(100)]
    public string OriginalAudioContentType { get; set; }

    /// <summary>
    /// Full path to the image blob file (folder/filename)
    /// </summary>
    [MaxLength(500)]
    public string ImageBlobPath { get; set; }

    /// <summary>
    /// Full path to the exact cover art supplied by the creator. Unlike <see cref="ImageBlobPath"/>
    /// this is never overwritten by a crop or a replacement, so the upload can always be recovered.
    /// </summary>
    [MaxLength(500)]
    public string OriginalCoverArtBlobPath { get; set; }

    /// <summary>
    /// Browser-supplied filename for the retained original cover art.
    /// </summary>
    [MaxLength(255)]
    public string OriginalCoverArtFileName { get; set; }

    /// <summary>
    /// Comma-separated widths of the pre-resized WebP renditions that exist for
    /// <see cref="ImageBlobPath"/>, e.g. <c>"128,320,640"</c>. Null or empty means none have been
    /// generated, which is why the feature degrades safely: every consumer falls back to serving
    /// the full-size blob, exactly as it did before renditions existed.
    ///
    /// A comma-separated list rather than a bitmask because a bitmask would be pinned to the
    /// *index order* of the width ladder — inserting a rung would silently change the meaning of
    /// every row already stored. It also has to express the never-upscale outcome, where a small
    /// source legitimately has fewer renditions than the ladder, or one at a non-ladder width.
    /// </summary>
    [MaxLength(64)]
    public string CoverArtVariantWidths { get; set; }

    /// <summary>
    /// Incremented every time the renditions are regenerated, and emitted as a <c>?v=</c> query
    /// parameter on the URLs. Cover art under the GUID naming scheme lives at a fixed path that a
    /// re-crop overwrites in place, and the media endpoint serves it with a year-long
    /// <c>immutable</c> cache header — without this, a recropped image would stay stale in browsers.
    /// </summary>
    public int CoverArtVariantVersion { get; set; }

    /// <summary>
    /// File extension (.mp3, .jpg, .jpeg, .png) - DEPRECATED
    /// </summary>
    [MaxLength(10)]
    public string FileExtension { get; set; } = string.Empty;

    /// <summary>
    /// The name of the album that this file belongs to
    /// </summary>
    [MaxLength(200)]
    public string AlbumName { get; set; }

    /// <summary>
    /// Indicates whether this image file is the cover art for an album
    /// </summary>
    public bool IsAlbumCover { get; set; }

    /// <summary>
    /// The genre of the song (e.g., Rock, Country, Pop)
    /// </summary>
    [MaxLength(50)]
    public string Genre { get; set; }

    /// <summary>
    /// The display title of the song. If null, the title is derived from the file name.
    /// </summary>
    [MaxLength(200)]
    public string SongTitle { get; set; }

    /// <summary>
    /// The track number for an album track (1-based index)
    /// </summary>
    public int? TrackNumber { get; set; }

    /// <summary>
    /// The track length in seconds
    /// </summary>
    public double? TrackLength { get; set; }

    /// <summary>
    /// The folder in the streaming container holding this song's encrypted HLS package, or null
    /// when it has not been packaged yet.
    ///
    /// <para>
    /// Null is the whole rollout mechanism: the backfill selects on it, so a run is resumable with
    /// no per-item state, and a song without a package simply is not playable through the encrypted
    /// path yet. Repackaging mints a <b>new</b> id rather than reusing this one, which is what makes
    /// a package immutable at its path — no cache-busting version is needed, and a half-written
    /// folder from a failed run can never be confused with the live one.
    /// </para>
    ///
    /// <para>
    /// Not <see cref="MediaGuid"/>, deliberately. Pre-July-2026 songs have no media GUID, and
    /// minting one for them would change what <c>SongMediaPaths.FacebookImageFor</c> resolves to
    /// and break share images already circulating.
    /// </para>
    /// </summary>
    public Guid? HlsStreamId { get; set; }

    /// <summary>
    /// This song's AES-128 content key, wrapped. Never the raw key — see <c>IHlsContentKeyProtector</c>.
    ///
    /// <para>
    /// Wrapped with a config-held key rather than ASP.NET Data Protection on purpose. The Data
    /// Protection ring is deliberately excluded from backup (see
    /// <c>StorageBackupService.GetConfiguredContainerNames</c>) because everything it protects is
    /// transient; a content key is neither transient nor reproducible, and losing the ring would
    /// mean re-encoding the entire catalogue.
    /// </para>
    /// </summary>
    [MaxLength(256)]
    public string HlsKeyProtected { get; set; }

    /// <summary>The package's initialisation vector, lowercase hex, as written into the manifest.</summary>
    [MaxLength(32)]
    public string HlsIv { get; set; }

    /// <summary>
    /// How many segments the package holds. Used to sanity-check a restored package against what
    /// storage actually contains, so a partial restore is caught by the audit rather than by a
    /// listener hearing a song cut off.
    /// </summary>
    public int? HlsSegmentCount { get; set; }

    /// <summary>The manifest's <c>#EXT-X-TARGETDURATION</c>, in seconds.</summary>
    public double? HlsTargetDurationSeconds { get; set; }

    /// <summary>When the current package was produced. Null exactly when <see cref="HlsStreamId"/> is.</summary>
    public DateTime? HlsPackagedAt { get; set; }

    /// <summary>
    /// Bumped whenever the playback audio at <see cref="Mp3BlobPath"/> is rewritten in place.
    ///
    /// <para>
    /// The audio counterpart of <see cref="CoverArtVariantVersion"/>, and it exists for the same
    /// reason: under the GUID scheme the playback blob lives at a fixed path, <c>MusicController</c>
    /// serves it with a year-long <c>immutable</c> cache header, and the MAUI app keys its offline
    /// cache on a hash of the path. Without a version there is nothing for either cache to notice,
    /// so a re-transcoded song would serve its old bytes to browsers for a year and to already-synced
    /// devices forever.
    /// </para>
    ///
    /// <para>
    /// Harmless until now only because nothing re-transcodes a published song. Emitted as
    /// <c>?v={n}</c> on audio URLs and as <c>AudioVersion</c> on the mobile DTO.
    /// </para>
    ///
    /// <para>
    /// <b>Starts at 0, not 1</b> — unlike <see cref="CoverArtVariantVersion"/>. The MAUI client folds
    /// the version into its cache key only when it is greater than zero
    /// (<c>StableRemoteAssetKey.GetPathHash</c>), so zero is the one value that hashes identically to
    /// the parameter never having existed. Shipping this column at 1 would change every audio cache
    /// key at once and silently re-download every user's entire offline library. Zero means "never
    /// rewritten since first publish", which is true of every existing row and every new upload; the
    /// first in-place rewrite makes it 1.
    /// </para>
    /// </summary>
    public int AudioContentVersion { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this song first became publicly visible, or null if it never has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="CreatedAt"/>, which records when the row was written. A song can
    /// exist while disabled, be enabled later, have its metadata or artwork edited afterwards, or
    /// be pulled and restored - and only the first moment it became publicly visible is a release.
    /// </para>
    /// <para>
    /// Stamped exactly once, by the release-notification job, and never re-stamped. That is what
    /// makes "do not notify on a draft, a re-crop, or a metadata edit" fall out for free instead
    /// of needing a rule per case. Existing rows were backfilled from CreatedAt by the migration
    /// that added this column.
    /// </para>
    /// </remarks>
    public DateTime? FirstPublishedAtUtc { get; set; }

    /// <summary>
    /// When this record was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The number of times this song has been streamed (played for at least the creator's configured continuous seconds)
    /// </summary>
    public int NumberOfStreams { get; set; }

    /// <summary>
    /// The number of streams at the time of the last payout.
    /// Used to calculate unpaid streams (NumberOfStreams - StreamsAtLastPayout).
    /// </summary>
    public int StreamsAtLastPayout { get; set; }

    /// <summary>
    /// Indicates whether this song or album should be displayed on the home page
    /// </summary>
    public bool DisplayOnHomePage { get; set; }

    /// <summary>
    /// Optional display order used by library-style surfaces.
    /// Null indicates a new or not-yet-ranked song and should sort ahead of ranked songs.
    /// </summary>
    public int? DisplayOrder { get; set; }

    /// <summary>
    /// Indicates whether this song's music was created using AI.
    /// </summary>
    public bool IsAiGenerated { get; set; }

    /// <summary>
    /// Indicates whether this song uses AI-generated vocals.
    /// </summary>
    public bool IsAiVocals { get; set; }

    /// <summary>
    /// Indicates whether this song uses AI-generated lyrics.
    /// </summary>
    public bool IsAiLyrics { get; set; }

    /// <summary>
    /// Indicates whether this song is active and available for playback.
    /// Inactive songs are not displayed anywhere on the website and cannot be played.
    /// Songs are set to inactive when a creator deletes them or closes their account.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Foreign key to the Creator who uploaded this song.
    /// If null, the song was uploaded by the platform admin.
    /// </summary>
    public int? CreatorId { get; set; }

    /// <summary>
    /// Navigation property to the Creator who owns this song.
    /// </summary>
    public virtual Creator Creator { get; set; }

    /// <summary>
    /// Indicates whether this song is enabled and available for playback and playlists.
    /// Disabled songs are hidden from the media library, cannot be played, and are removed from playlists.
    /// Songs are disabled by admin when content violates terms or policies.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The reason for the most recent status change (enable or disable) by admin.
    /// Required when enabling or disabling a song.
    /// </summary>
    [MaxLength(1000)]
    public string StatusReason { get; set; }

    /// <summary>
    /// The artist name for this song. If set, overrides the creator's display name.
    /// Priority for display: Persona.Name > ArtistName > Creator.DisplayName > Creator.User.Email
    /// </summary>
    [MaxLength(20)]
    public string ArtistName { get; set; }

    /// <summary>
    /// Optional foreign key to the CreatorPersona associated with this song.
    /// When set, the persona name takes highest priority for artist display.
    /// </summary>
    public int? PersonaId { get; set; }

    /// <summary>
    /// Navigation property to the persona associated with this song.
    /// Will be null when PersonaId is null (no persona associated).
    /// </summary>
    public virtual CreatorPersona Persona { get; set; }

    /// <summary>
    /// The width of the image in pixels. Populated during upload or crop operations.
    /// </summary>
    public int? ImageWidth { get; set; }

    /// <summary>
    /// The height of the image in pixels. Populated during upload or crop operations.
    /// </summary>
    public int? ImageHeight { get; set; }

    /// <summary>
    /// Returns true if the image is a perfect square, false if not square,
    /// or null if dimensions are unknown.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool? IsImageSquare =>
        (ImageWidth.HasValue && ImageHeight.HasValue)
            ? ImageWidth.Value == ImageHeight.Value
            : null;

    /// <summary>
    /// Gets the effective artist name using the priority:
    /// 1. Persona.Name (if a persona is linked)
    /// 2. SongMetadata.ArtistName
    /// 3. Creator.DisplayName
    /// 4. Creator.User.Email (part before @)
    /// </summary>
    public string GetEffectiveArtistName()
    {
        // Priority 1: Persona name (only if persona is enabled)
        if (Persona != null && Persona.IsEnabled && !string.IsNullOrWhiteSpace(Persona.Name))
        {
            return Persona.Name;
        }

        // Priority 2: ArtistName from SongMetadata
        if (!string.IsNullOrWhiteSpace(ArtistName))
        {
            // Strip email domain if it contains @ to avoid exposing email addresses
            return ArtistName.Contains('@') ? ArtistName.Split('@')[0] : ArtistName;
        }

        // Priority 3: DisplayName from Creator
        if (Creator != null && !string.IsNullOrWhiteSpace(Creator.DisplayName))
        {
            return Creator.DisplayName;
        }

        // Priority 4: Email from Creator's User - use part before @ symbol
        if (Creator?.User?.Email != null)
        {
            return Creator.User.Email.Split('@')[0];
        }

        return string.Empty;
    }

    /// <summary>
    /// Gets the effective artist name for admin/management views using the priority:
    /// 1. Persona.Name (if a persona is linked)
    /// 2. SongMetadata.ArtistName
    /// 3. Creator.DisplayName
    /// 4. Creator.User.Email (full email address)
    /// Used in admin and creator song management grids/forms.
    /// </summary>
    public string GetEffectiveArtistNameFull()
    {
        // Priority 1: Persona name (only if persona is enabled)
        if (Persona != null && Persona.IsEnabled && !string.IsNullOrWhiteSpace(Persona.Name))
        {
            return Persona.Name;
        }

        // Priority 2: ArtistName from SongMetadata
        if (!string.IsNullOrWhiteSpace(ArtistName))
        {
            return ArtistName;
        }

        // Priority 3: DisplayName from Creator
        if (Creator != null && !string.IsNullOrWhiteSpace(Creator.DisplayName))
        {
            return Creator.DisplayName;
        }

        // Priority 4: Email from Creator's User - full email address for admin views
        if (Creator?.User?.Email != null)
        {
            return Creator.User.Email;
        }

        return string.Empty;
    }
}
