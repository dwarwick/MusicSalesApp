namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Names of the system-generated playlists that are identified by name rather than by a column.
/// </summary>
/// <remarks>
/// <c>Playlist</c> has an <c>IsSystemGenerated</c> flag but no discriminator saying WHICH system
/// playlist a row is, so the name is the lookup key - written by <c>PlaylistService</c> when the row
/// is created and read back by the web My Playlists page and the mobile controller. A literal in any
/// one of those places silently stops matching, which is precisely what AGENTS.md requires a shared
/// constant for.
/// </remarks>
public static class PlaylistNames
{
    public const string LikedSongs = "Liked Songs";
}
