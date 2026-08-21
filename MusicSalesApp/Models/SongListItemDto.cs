namespace MusicSalesApp.Models;

/// <summary>
/// DTO returned by the songs list API endpoint for the MAUI Android app.
/// Contains all metadata needed to display a song card and play the song.
/// </summary>
public class SongListItemDto
{
    public int Id { get; set; }
    public string SongTitle { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    /// <summary>
    /// The full-size cover art. Never repointed at a rendition: every mobile build already in the
    /// wild depends on this meaning exactly what it always has.
    /// </summary>
    public string AlbumArtUrl { get; set; }

    /// <summary>
    /// The small cover-art rendition the app caches for every song - list rows, the mini player,
    /// lock-screen artwork and the offline fallback. Null when no rendition exists, in which case
    /// clients fall back to <see cref="AlbumArtUrl"/>.
    /// </summary>
    public string AlbumArtThumbUrl { get; set; }

    /// <summary>
    /// The larger cover-art rendition for song cards and the player hero. Cached on a best-effort
    /// basis; null when no rendition exists.
    /// </summary>
    public string AlbumArtHeroUrl { get; set; }

    /// <summary>
    /// Incremented by the server whenever this song's cover art is rewritten. Clients fold it into
    /// their cache key: the GUID naming scheme keeps cover art at a fixed path that a re-crop
    /// overwrites in place, so the URL alone cannot distinguish new pixels from old.
    /// </summary>
    public int AlbumArtVersion { get; set; }

    /// <summary>Full-size persona image. As with <see cref="AlbumArtUrl"/>, never repointed.</summary>
    public string PersonaImageUrl { get; set; }

    /// <summary>The small persona-image rendition, or null when none exists.</summary>
    public string PersonaImageThumbUrl { get; set; }

    /// <summary>
    /// The larger persona-image rendition, or null when none exists. The player pages show the
    /// persona at 120 DIP, which needs 360 px on a 3x screen - more than the thumb carries.
    /// </summary>
    public string PersonaImageHeroUrl { get; set; }

    /// <summary>As <see cref="AlbumArtVersion"/>, for the persona image.</summary>
    public int PersonaImageVersion { get; set; }

    public string PersonaBio { get; set; }

    /// <summary>
    /// The persona's own website, or null when they have not given one.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="PersonaBio"/> there is NO creator-level fallback, because a Creator has
    /// no website column - only a persona can have one. Stored exactly as the creator typed it,
    /// with no scheme normalisation, so a client must not assume it begins with https://.
    /// </remarks>
    public string PersonaWebsiteUrl { get; set; }

    /// <summary>
    /// The blob path of this song's word-level lyric timings, or null when a listener may not
    /// see any. Fetch it from <c>api/music/{path}?v={LyricsVersion}</c>.
    /// </summary>
    /// <remarks>
    /// <b>The status is deliberately not shipped, only the resolved path.</b> Timings held back
    /// for review sit at exactly the blob path a published song would use, so a client handed a
    /// path plus a status would 404 on it anyway - and would have been told that a withheld
    /// alignment exists, which is not its business.
    /// </remarks>
    public string LyricsTimingsPath { get; set; }

    /// <summary>
    /// As <see cref="AlbumArtVersion"/>, for the lyric timings.
    /// </summary>
    /// <remarks>
    /// Load-bearing: the timings blob path never changes between re-publishes and the response
    /// carries a year-long immutable cache header, so without this a creator's corrected timings
    /// would be invisible forever to any client that had already fetched the old ones.
    /// </remarks>
    public int LyricsVersion { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public int StreamCount { get; set; }
    public int StreamQualifyingSeconds { get; set; }
    public double? TrackLengthSeconds { get; set; }
    public bool DisplayOnHomePage { get; set; }
    public int? DisplayOrder { get; set; }
    public bool IsAiGenerated { get; set; }
    public bool IsAiVocals { get; set; }
    public bool IsAiLyrics { get; set; }
    /// <summary>
    /// The Creator.Id that owns this song.
    /// Used by mobile clients to create tracked tip orders.
    /// </summary>
    public int? CreatorId { get; set; }
    /// <summary>
    /// The ApplicationUser.Id of the creator who uploaded this song.
    /// Used by mobile clients to detect "creator listening to own song" for playback rules.
    /// </summary>
    public int? CreatorUserId { get; set; }
}
