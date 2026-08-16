#nullable enable

namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// The rules a timings document has to satisfy, in two deliberately different strengths.
///
/// <para>
/// <b><see cref="Normalize"/> repairs; <see cref="Validate"/> refuses.</b> Keeping them apart is the
/// whole design. A creator halfway through re-tapping a chorus has a document where line four now
/// starts before line three ends — that is not an error, it is what the middle of an edit looks
/// like, and refusing it would make the editor unusable. So every edit is followed by
/// <see cref="Normalize"/>, which quietly tidies overlaps and clamps strays, and only Publish calls
/// <see cref="Validate"/>, which is the last gate before listeners see anything.
/// </para>
///
/// <para>
/// Pure and infrastructure-free on purpose, exactly like
/// <c>LyricsAlignmentCompletionService.Classify</c>: no EF, no storage, no Function, no Python. That
/// is what makes the rules cheap enough to test exhaustively, which matters because they are the
/// only thing standing between a mis-edited document and a karaoke display that drifts.
/// </para>
/// </summary>
public static class LyricsTimingsValidator
{
    /// <summary>
    /// How far past the song's duration a timing may sit before it is considered broken.
    ///
    /// <para>
    /// Not zero, because the duration is measured from a decode and the aligner works from a
    /// separately reassembled vocal stem; a few tens of milliseconds of disagreement at the very end
    /// of a track is normal and harmless. Shared with the pipeline's own structural gate so the two
    /// cannot answer the same question differently — a document the pipeline accepted must not be
    /// one the editor then refuses to publish.
    /// </para>
    /// </summary>
    public const double DurationOvershootTolerance = 1.05;

    /// <summary>
    /// Tidy a document into a self-consistent state, in place. Never throws, never refuses.
    ///
    /// <para>
    /// Applied after every edit. The operations are ordered so each one's output satisfies the next:
    /// clamp into the song, straighten each word, fit the words inside their line, then straighten
    /// the line itself.
    /// </para>
    /// </summary>
    public static void Normalize(LyricsTimingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var duration = document.DurationMs > 0 ? document.DurationMs : long.MaxValue;

        foreach (var line in document.Lines)
        {
            foreach (var word in line.Words)
            {
                word.StartMs = Clamp(word.StartMs, duration);
                word.EndMs = Clamp(word.EndMs, duration);

                // A word that ends before it starts is a zero-length word, not a negative one.
                if (word.StartMs.HasValue && word.EndMs.HasValue && word.EndMs < word.StartMs)
                {
                    word.EndMs = word.StartMs;
                }
            }

            line.StartMs = Clamp(line.StartMs, duration);
            line.EndMs = Clamp(line.EndMs, duration);

            // A line with no span cannot contain words with one - that combination is what makes a
            // section marker light up during the intro.
            if (!line.IsTimed)
            {
                foreach (var word in line.Words)
                {
                    word.StartMs = null;
                    word.EndMs = null;
                }

                continue;
            }

            // The line has to be wide enough to hold its own words; widening the line is right and
            // squeezing the words is not, because the words are what the creator actually placed.
            var timedWords = line.Words.Where(word => word.IsTimed).ToList();
            if (timedWords.Count > 0)
            {
                line.StartMs = Math.Min(line.StartMs!.Value, timedWords.Min(word => word.StartMs!.Value));
                line.EndMs = Math.Max(line.EndMs!.Value, timedWords.Max(word => word.EndMs!.Value));
            }

            if (line.EndMs < line.StartMs)
            {
                line.EndMs = line.StartMs;
            }
        }
    }

    /// <summary>
    /// Everything wrong with a document, in words a creator can act on. Empty means publishable.
    ///
    /// <para>
    /// Messages are deliberately free of jargon — no "monotonic", no "milliseconds", no field names.
    /// They are rendered straight into the editor when Publish is refused, and a creator who is told
    /// "timings are not monotonic at index 47" has been told nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Validate(LyricsTimingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var problems = new List<string>();

        if (document.Lines.Count == 0)
        {
            problems.Add("There are no lyrics to publish.");
            return problems;
        }

        if (!document.Lines.Any(line => line.IsTimed))
        {
            problems.Add("None of these lines have a time against them yet.");
            return problems;
        }

        var ceiling = document.DurationMs > 0
            ? (long)(document.DurationMs * DurationOvershootTolerance)
            : long.MaxValue;

        long previousEnd = -1;
        var previousDescription = string.Empty;

        for (var index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            var label = Describe(line, index);

            if (line.StartMs.HasValue != line.EndMs.HasValue)
            {
                problems.Add($"{label} has only half a time against it.");
                continue;
            }

            if (!line.IsTimed)
            {
                if (line.Words.Any(word => word.IsTimed))
                {
                    problems.Add($"{label} is marked as not sung, but some of its words have times.");
                }

                continue;
            }

            if (line.EndMs < line.StartMs)
            {
                problems.Add($"{label} ends before it starts.");
            }

            if (line.EndMs > ceiling)
            {
                problems.Add($"{label} runs past the end of the song.");
            }

            foreach (var word in line.Words.Where(word => word.IsTimed))
            {
                if (word.StartMs < line.StartMs || word.EndMs > line.EndMs)
                {
                    problems.Add($"In {LowerFirst(label)}, \"{word.Text}\" sits outside the line's own timing.");
                    break;
                }
            }

            // Monotonic across the whole document, not just inside a line: two lines that overlap
            // make the highlight jump backwards, which reads as the song skipping.
            if (previousEnd >= 0 && line.StartMs < previousEnd)
            {
                problems.Add($"{label} starts before {LowerFirst(previousDescription)} has finished.");
            }

            previousEnd = line.EndMs!.Value;
            previousDescription = label;
        }

        return problems;
    }

    private static long? Clamp(long? value, long duration)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return Math.Clamp(value.Value, 0, duration);
    }

    private static string Describe(LyricsTimedLine line, int index)
    {
        var text = line.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            return $"Line {index + 1}";
        }

        return text.Length <= 30
            ? $"\"{text}\""
            : $"\"{text[..30]}…\"";
    }

    private static string LowerFirst(string value) =>
        string.IsNullOrEmpty(value) || !char.IsUpper(value[0])
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];
}
