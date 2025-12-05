namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Contains default price constants used throughout the application.
/// </summary>
public static class PriceDefaults
{
    /// <summary>
    /// Default price for an individual song when not specified in index tags.
    /// </summary>
    public const decimal DefaultSongPrice = 0.99m;

    /// <summary>
    /// Default price for an album when not specified in index tags.
    /// </summary>
    public const decimal DefaultAlbumPrice = 9.99m;
}
