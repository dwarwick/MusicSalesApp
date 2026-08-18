using System.Text.Json.Serialization;

#nullable enable

namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// The word-level timing document: the artifact a karaoke display actually reads.
///
/// <para>
/// <b>This is the C# side of a contract that has only ever had a Python side.</b> The document is
/// produced by <c>MusicSalesApp.LyricsFunctions/lyrics/formats.py::to_timing_json</c>, uploaded as
/// <c>{guid}-lyrics.json</c>, and until now was never deserialised anywhere — the web app copied the
/// blob from staging to media without ever looking inside it. Every field here mirrors that writer,
/// and <c>LyricsTimingsRoundTripTests</c> asserts the two agree against a committed fixture, because
/// nothing else can: the producer is in another language, in another repository directory, deployed
/// separately.
/// </para>
///
/// <para>
/// <b>Times are nullable, and that is the single most important decision in this file.</b> A lyric
/// sheet contains lines nobody sings — blank lines separating verses, and section markers like
/// <c>[Chorus]</c> or <c>Final Chorus</c>. Those are kept in the document rather than dropped,
/// because they are part of how the artist laid the song out and a display that silently removed
/// them would not be showing the lyrics that were submitted. They carry <c>null</c> times. A
/// non-nullable <c>long</c> would deserialise every one of them as <c>0</c>, which reads as "sung at
/// the very start of the song": the display would light up every section heading during the intro,
/// and the monotonicity check would then reject the document on the way back out.
/// </para>
/// </summary>
public sealed class LyricsTimingsDocument
{
    /// <summary>The song these timings belong to. Written for traceability, not used for lookup.</summary>
    public int SongId { get; set; }

    /// <summary>
    /// The song's full duration in milliseconds, as measured from the decoded audio.
    ///
    /// <para>
    /// The timeline every timing in this document is clamped to. It comes from the original decode
    /// rather than from the reassembled vocal stem, because these timings are played back against
    /// the original MP3.
    /// </para>
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// The composite confidence the aligner reported, 0-1.
    ///
    /// <para>
    /// Advisory only. It colours what the creator is told and where they are pointed first; it does
    /// not decide what listeners see, because a creator presses Publish for that.
    /// </para>
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>Every line of the artist's sheet, in order, sung or not.</summary>
    public List<LyricsTimedLine> Lines { get; set; } = new();
}

/// <summary>One line of the artist's lyrics, with the span it occupies and the words inside it.</summary>
public sealed class LyricsTimedLine
{
    /// <summary>
    /// The line exactly as the artist typed it — original capitalisation and punctuation.
    ///
    /// <para>
    /// Normalisation happens on the matching side only. What is displayed is what was submitted.
    /// </para>
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>When the line starts, or null for a line nobody sings.</summary>
    public long? StartMs { get; set; }

    /// <summary>When the line ends, or null for a line nobody sings.</summary>
    public long? EndMs { get; set; }

    /// <summary>
    /// The words of the line, each with its own span.
    ///
    /// <para>
    /// Empty for an untimed line. Also empty for a line the aligner could place but whose words it
    /// could not — the line span is then usable even though the per-word highlight is not.
    /// </para>
    /// </summary>
    public List<LyricsTimedWord> Words { get; set; } = new();

    /// <summary>Whether this line has a usable span. Untimed lines are section markers and blanks.</summary>
    /// <remarks>
    /// Ignored on the wire. It is a convenience for C# callers; serialising it would add a key the
    /// Python writer has never heard of, so a document this app had merely read and rewritten would
    /// no longer match a freshly aligned one.
    /// </remarks>
    [JsonIgnore]
    public bool IsTimed => StartMs.HasValue && EndMs.HasValue;
}

/// <summary>One word, and the moment it is sung.</summary>
public sealed class LyricsTimedWord
{
    /// <summary>The word as the artist typed it, punctuation included.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>When the word starts, or null if it could not be placed.</summary>
    public long? StartMs { get; set; }

    /// <summary>When the word ends, or null if it could not be placed.</summary>
    public long? EndMs { get; set; }

    /// <summary>Whether this word can be highlighted.</summary>
    [JsonIgnore]
    public bool IsTimed => StartMs.HasValue && EndMs.HasValue;
}
