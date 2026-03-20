namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for artist display name fallback values.
/// Use these constants instead of inline strings wherever an artist name
/// is produced as a fallback and compared/displayed elsewhere, to prevent
/// silent drift if the text changes.
/// </summary>
public static class ArtistDisplayNames
{
    public const string UnknownArtist = "Unknown Artist";
}
