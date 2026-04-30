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
    public string? PersonaImageUrl { get; set; }
    public string? PersonaBio { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public int StreamCount { get; set; }
    public double? TrackLengthSeconds { get; set; }
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
