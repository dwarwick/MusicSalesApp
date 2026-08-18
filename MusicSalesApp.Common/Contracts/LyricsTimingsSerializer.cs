using System.Text.Json;
#nullable enable

namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// Reads and writes <see cref="LyricsTimingsDocument"/> in the exact shape the Python writer emits.
///
/// <para>
/// <b>One options instance, shared, so the reader and the writer cannot drift.</b> The document is
/// read from a blob some other process wrote and written back to the same path after a creator edits
/// it, so a mismatch between the two is not a serialisation detail — it is a song whose timings
/// silently change shape the first time anybody touches them.
/// </para>
///
/// <para>
/// <c>JsonSerializerDefaults.Web</c> is chosen rather than assembled by hand because it is what the
/// rest of the site already uses: camelCase names on the way out, case-insensitive matching on the
/// way in. What matters just as much is what it does <b>not</b> set —
/// <c>DefaultIgnoreCondition</c> stays at <c>Never</c>, so a null <c>startMs</c> is written as
/// <c>null</c> rather than omitted. Omitting it would still round-trip through this class, and would
/// still deserialise correctly, and would still be wrong: the Python side writes the key, so a
/// document this class had rewritten would no longer be byte-comparable with a freshly aligned one,
/// and the fixture test that guards this contract would be the only thing that ever noticed.
/// </para>
/// </summary>
public static class LyricsTimingsSerializer
{
    /// <summary>The one options instance. Do not construct another.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Parse a timings document, or null if the payload is empty or unreadable.</summary>
    /// <remarks>
    /// Returns null rather than throwing for an unreadable document, because every caller is
    /// answering "can this song show lyrics" and the answer for a corrupt blob is simply no. A
    /// throw here would take out the song page.
    /// </remarks>
    public static LyricsTimingsDocument? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LyricsTimingsDocument>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serialise a timings document in the writer's shape.</summary>
    public static string Serialize(LyricsTimingsDocument document) =>
        JsonSerializer.Serialize(document, Options);
}
