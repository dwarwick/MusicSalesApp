namespace MusicSalesApp.Models;

#nullable enable

/// <summary>
/// Kind of playlist for display in the MAUI app.
/// </summary>
public static class MobilePlaylistKinds
{
    public const string Custom = "Custom";
    public const string LikedSongs = "LikedSongs";
    public const string Recommended = "Recommended";
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
}

/// <summary>
/// Home page dynamic playlists returned to the MAUI app.
/// Either value may be null when the user has no corresponding content.
/// </summary>
public class MobileHomePlaylistsDto
{
    public MobilePlaylistDto? Recommended { get; set; }
    public MobilePlaylistDto? LikedSongs { get; set; }
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
    public string StreamUrl { get; set; } = string.Empty;
    public int StreamCount { get; set; }
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
