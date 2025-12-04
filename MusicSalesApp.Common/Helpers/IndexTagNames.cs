namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Contains constant strings for blob index tag names used throughout the application.
/// </summary>
public static class IndexTagNames
{
    /// <summary>
    /// The name of the album that this file belongs to.
    /// </summary>
    public const string AlbumName = "AlbumName";

    /// <summary>
    /// Indicates whether this image file is the cover art for an album.
    /// Value should be "true" or "false".
    /// </summary>
    public const string IsAlbumCover = "IsAlbumCover";

    /// <summary>
    /// The price of the item. Reserved for future use.
    /// </summary>
    public const string Price = "Price";

    /// <summary>
    /// The price of an album.
    /// </summary>
    public const string AlbumPrice = "AlbumPrice";

    /// <summary>
    /// The price of a song.
    /// </summary>
    public const string SongPrice = "SongPrice";

    /// <summary>
    /// The genre of the song (e.g., Rock, Country, Pop).
    /// </summary>
    public const string Genre = "Genre";
}
