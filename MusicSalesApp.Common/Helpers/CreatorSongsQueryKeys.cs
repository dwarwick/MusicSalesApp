namespace MusicSalesApp.Common.Helpers;

public static class CreatorSongsQueryKeys
{
    /// <summary>
    /// A song id, asking the grid to open the lyrics paste box for it ready to replace its words.
    /// </summary>
    /// <remarks>
    /// Exists because the paste box is deliberately not offered once a song has timings - re-running
    /// the same words buys nothing but another separation pass. Replacing WRONG words is a different
    /// request, and this is how Preview Lyrics makes it, since that is where a creator discovers the
    /// aligner faithfully timed the wrong lyrics.
    /// </remarks>
    public const string ReplaceLyrics = "replace_lyrics";
}
