namespace MusicSalesApp.Models;

#nullable enable

/// <summary>
/// Kind of playlist for display in the MAUI app.
///
/// <para>
/// Mirrored by <c>MusicSalesApp.Maui.ViewModels.PlaylistKinds</c>; the two must stay in step. The
/// playlist WINDOW keys, which are the newer and more load-bearing contract - they are simultaneously a
/// database value, a URL segment and a mobile route parameter - live in
/// <c>MusicSalesApp.Common.Helpers.TopStreamedWindows</c> instead, where both repos read the one
/// copy.
/// </para>
/// </summary>
public static class MobilePlaylistKinds
{
    public const string Custom = "Custom";
    public const string LikedSongs = "LikedSongs";
    public const string Recommended = "Recommended";

    /// <summary>One of the five global "most streamed" playlists. Carries a <c>Key</c>; its <c>Id</c> is 0.</summary>
    public const string TopStreamed = "TopStreamed";
}

/// <summary>
/// Summary playlist DTO for list views in the MAUI app.
/// </summary>
public class MobilePlaylistDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SongCount { get; set; }
    public bool IsSystemGenerated { get; set; }
    public string Kind { get; set; } = MobilePlaylistKinds.Custom;

    /// <summary>
    /// For a <see cref="MobilePlaylistKinds.TopStreamed"/> playlist, its
    /// <c>TopStreamedWindows</c> key; null for every other kind.
    ///
    /// <para>
    /// These five have no row of their own, so <see cref="Id"/> is 0 for all of them - the same
    /// value Recommended already uses. The client must therefore open them by <b>Key</b>, never by
    /// id, or all six land on the same broken page.
    /// </para>
    /// </summary>
    public string? Key { get; set; }

    /// <summary>Server-dictated position when several playlists are listed together. Lower first.</summary>
    public int DisplayOrder { get; set; }
}

/// <summary>
/// Home page dynamic playlists returned to the MAUI app.
/// Either single value may be null when the user has no corresponding content.
/// </summary>
public class MobileHomePlaylistsDto
{
    public MobilePlaylistDto? Recommended { get; set; }
    public MobilePlaylistDto? LikedSongs { get; set; }

    /// <summary>
    /// The five global "most streamed" playlists, already in display order, with empty ones omitted.
    ///
    /// <para>
    /// A list rather than five more nullable properties so the server owns the order and the client
    /// renders whatever it is handed. Unlike the two above these are not personal, so they are
    /// populated for signed-out callers too.
    /// </para>
    /// </summary>
    public List<MobilePlaylistDto> TopStreamed { get; set; } = new();
}

/// <summary>
/// A song returned as part of a playlist response. Includes UserPlaylistId so
/// the MAUI app can target individual entries for remove and reorder operations.
/// UserPlaylistId is null for dynamic lists (recommended / genre / artist).
/// </summary>
public class MobilePlaylistSongDto
{
    public int? UserPlaylistId { get; set; }
    public int SongMetadataId { get; set; }
    public string SongTitle { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string? AlbumArtUrl { get; set; }

    /// <summary>Small cover-art rendition; null when none exists. See <see cref="SongListItemDto"/>.</summary>
    public string? AlbumArtThumbUrl { get; set; }

    /// <summary>Larger cover-art rendition for the player hero; null when none exists.</summary>
    public string? AlbumArtHeroUrl { get; set; }

    /// <summary>Cache-busting counter for the cover art. See <see cref="SongListItemDto"/>.</summary>
    public int AlbumArtVersion { get; set; }

    public string? PersonaImageUrl { get; set; }

    /// <summary>Small persona-image rendition; null when none exists.</summary>
    public string? PersonaImageThumbUrl { get; set; }

    /// <summary>
    /// Larger persona-image rendition; null when none exists. The player pages show the persona at
    /// 120 DIP, which needs 360 px on a 3x screen - more than the thumb carries.
    /// </summary>
    public string? PersonaImageHeroUrl { get; set; }

    /// <summary>Cache-busting counter for the persona image.</summary>
    public int PersonaImageVersion { get; set; }

    public string? PersonaBio { get; set; }

    /// <summary>
    /// The persona's own website, or null. No creator-level fallback exists - only a persona
    /// can have one - and the value is stored exactly as typed, so do not assume a scheme.
    /// </summary>
    public string? PersonaWebsiteUrl { get; set; }

    /// <summary>
    /// The blob path of this song's word-level lyric timings, or null when a listener may not
    /// see any. Fetch from <c>api/music/{path}?v={LyricsVersion}</c>. Only ever set for
    /// published lyrics; the status itself is deliberately not shipped.
    /// </summary>
    public string? LyricsTimingsPath { get; set; }

    /// <summary>Cache-buster for the timings, whose blob path never changes between publishes.</summary>
    public int LyricsVersion { get; set; }
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// The encrypted-HLS manifest URL, or null when this song has not been packaged yet.
    ///
    /// <para>
    /// Sits alongside <c>StreamUrl</c> rather than replacing it, because the mobile app still plays
    /// the MP3 and a released app cannot be updated in step with the server. A client that
    /// understands this field should prefer it; one that does not keeps working unchanged.
    /// </para>
    /// </summary>
    public string? HlsUrl { get; set; }

    /// <summary>
    /// Bumped whenever the playback audio at the same blob path is rewritten, so a client caching by
    /// path can tell that the bytes changed.
    ///
    /// <para>
    /// <b>Zero means "never rewritten", and must keep hashing identically to this field not being
    /// sent at all.</b> The MAUI client folds the version into its audio cache key only when it is
    /// greater than zero; starting this at 1 would change every cache key at once and silently
    /// re-download every user's offline library.
    /// </para>
    /// </summary>
    public int AudioVersion { get; set; }

    /// <summary>Lifetime streams. Kept live by the stream-count hub.</summary>
    public int StreamCount { get; set; }

    /// <summary>
    /// Streams inside this list's period, or null when the list has no period.
    ///
    /// <para>
    /// This is the number the top-streamed playlists are RANKED on, whereas
    /// <see cref="StreamCount"/> is the lifetime total the player displays and keeps live. On "Top 10
    /// Today" the two differ, so a client showing only the lifetime figure would render a correctly
    /// ordered list that looks mis-sorted. Show both.
    /// </para>
    ///
    /// <para>
    /// A snapshot taken when the playlist was last generated, so it does not move during the day.
    /// </para>
    /// </summary>
    public int? PeriodStreamCount { get; set; }
    public int StreamQualifyingSeconds { get; set; }
    public double? TrackLengthSeconds { get; set; }
    public bool DisplayOnHomePage { get; set; }
    public int? DisplayOrder { get; set; }
    public bool IsAiGenerated { get; set; }
    public bool IsAiVocals { get; set; }
    public bool IsAiLyrics { get; set; }
    public int? CreatorId { get; set; }
    public int? CreatorUserId { get; set; }
}

public class MobilePlaylistSongsDto
{
    public int PlaylistId { get; set; }
    public string PlaylistName { get; set; } = string.Empty;
    public bool IsSystemGenerated { get; set; }
    public List<MobilePlaylistSongDto> Songs { get; set; } = new();

    /// <summary>
    /// Heading for each song's <see cref="MobilePlaylistSongDto.PeriodStreamCount"/> - "Today",
    /// "This Week" and so on - or null when the list has no period of its own.
    ///
    /// <para>
    /// Null for every playlist except the four rolling top-streamed ones. The all-time playlist
    /// leaves it null on purpose: its ranking number and the lifetime counter are the same figure, so
    /// a second column would just repeat the first.
    /// </para>
    /// </summary>
    public string? PeriodLabel { get; set; }
}

public class CreateMobilePlaylistRequest
{
    public string Name { get; set; } = string.Empty;
}

public class RenameMobilePlaylistRequest
{
    public string Name { get; set; } = string.Empty;
}

public class AddSongToMobilePlaylistRequest
{
    public int SongMetadataId { get; set; }
}

public class ReorderMobilePlaylistRequest
{
    public List<int> UserPlaylistIds { get; set; } = new();
}
