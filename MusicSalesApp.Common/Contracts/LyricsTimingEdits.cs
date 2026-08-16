#nullable enable

namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// The edits a creator can make to a timings document, as pure functions over the document.
///
/// <para>
/// No UI, no storage, no interop — which is what makes the arithmetic here testable, and the
/// arithmetic is the part that decides whether tapping along to a song produces something usable or
/// a mess. The editor page is a thin shell over these.
/// </para>
///
/// <para>
/// Every operation leaves the document normalised, so a caller can apply them back to back without
/// thinking about the intermediate states.
/// </para>
/// </summary>
public static class LyricsTimingEdits
{
    /// <summary>How long a line is assumed to last when there is nothing at all to infer from.</summary>
    private const long FallbackLineSpanMs = 2_000;

    /// <summary>
    /// Move a line's start to <paramref name="newStartMs"/> — the tap-along operation.
    ///
    /// <para>
    /// Three things happen, and the third is the one that makes a single pass down the song produce
    /// a coherent result rather than a pile of overlaps:
    /// </para>
    ///
    /// <list type="number">
    /// <item>the line starts at the tapped moment;</item>
    /// <item>its words are re-spread across the new span, keeping their relative positions, so a
    /// creator who taps one line start does not have to place its six words by hand;</item>
    /// <item><b>the previous line is implicitly ended here.</b> A creator tapping in time with the
    /// music is telling us where line four begins, which is also the best information anyone has
    /// about where line three stopped. Without this, re-tapping a chorus leaves every previous line
    /// still running underneath it and two lines highlight at once.</item>
    /// </list>
    /// </summary>
    public static void RetimeLine(LyricsTimingsDocument document, int lineIndex, long newStartMs)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (lineIndex < 0 || lineIndex >= document.Lines.Count)
        {
            return;
        }

        var line = document.Lines[lineIndex];
        var duration = document.DurationMs > 0 ? document.DurationMs : long.MaxValue;
        newStartMs = Math.Clamp(newStartMs, 0, duration);

        var newEndMs = ChooseEnd(document, lineIndex, line, newStartMs, duration);

        RescaleLine(line, newStartMs, newEndMs);
        EndPreviousLineAt(document, lineIndex, newStartMs);

        LyricsTimingsValidator.Normalize(document);
    }

    /// <summary>Move one word earlier or later, keeping its length.</summary>
    /// <remarks>
    /// The word is moved, not stretched: a creator nudging "focus" means it is sung slightly later,
    /// not that it is sung for longer. Any resulting overlap with the line's own span is tidied by
    /// <see cref="LyricsTimingsValidator.Normalize"/>, which widens the line rather than clipping the
    /// word — the word is what the creator just placed deliberately.
    /// </remarks>
    public static void NudgeWord(LyricsTimingsDocument document, int lineIndex, int wordIndex, long deltaMs)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (lineIndex < 0 || lineIndex >= document.Lines.Count)
        {
            return;
        }

        var line = document.Lines[lineIndex];
        if (wordIndex < 0 || wordIndex >= line.Words.Count)
        {
            return;
        }

        var word = line.Words[wordIndex];
        if (!word.IsTimed)
        {
            return;
        }

        word.StartMs += deltaMs;
        word.EndMs += deltaMs;

        LyricsTimingsValidator.Normalize(document);
    }

    /// <summary>Move a whole line, and everything in it, earlier or later.</summary>
    public static void NudgeLine(LyricsTimingsDocument document, int lineIndex, long deltaMs)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (lineIndex < 0 || lineIndex >= document.Lines.Count)
        {
            return;
        }

        var line = document.Lines[lineIndex];
        if (!line.IsTimed)
        {
            return;
        }

        line.StartMs += deltaMs;
        line.EndMs += deltaMs;

        foreach (var word in line.Words.Where(word => word.IsTimed))
        {
            word.StartMs += deltaMs;
            word.EndMs += deltaMs;
        }

        LyricsTimingsValidator.Normalize(document);
    }

    /// <summary>A deep copy, for undo stacks and for "reset to how we found it".</summary>
    public static LyricsTimedLine CopyLine(LyricsTimedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new LyricsTimedLine
        {
            Text = line.Text,
            StartMs = line.StartMs,
            EndMs = line.EndMs,
            Words = line.Words
                .Select(word => new LyricsTimedWord
                {
                    Text = word.Text,
                    StartMs = word.StartMs,
                    EndMs = word.EndMs
                })
                .ToList()
        };
    }

    /// <summary>A deep copy of the whole document.</summary>
    public static LyricsTimingsDocument CopyDocument(LyricsTimingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new LyricsTimingsDocument
        {
            SongId = document.SongId,
            DurationMs = document.DurationMs,
            Confidence = document.Confidence,
            Lines = document.Lines.Select(CopyLine).ToList()
        };
    }

    /// <summary>Where a re-timed line should now end.</summary>
    private static long ChooseEnd(
        LyricsTimingsDocument document,
        int lineIndex,
        LyricsTimedLine line,
        long newStartMs,
        long duration)
    {
        // If a later line is already placed after this tap, run up to it. During a record pass that
        // is usually the untouched original timing of the next line, which is exactly the right
        // guess: the creator is about to tap it and correct us anyway.
        for (var index = lineIndex + 1; index < document.Lines.Count; index++)
        {
            var candidate = document.Lines[index];
            if (candidate.IsTimed && candidate.StartMs > newStartMs)
            {
                return Math.Min(candidate.StartMs!.Value, duration);
            }
        }

        // Otherwise keep the length the line already had - the aligner's guess at how long the line
        // takes to sing is usually better than ours, even when its placement was wrong.
        if (line.IsTimed && line.EndMs > line.StartMs)
        {
            return Math.Min(newStartMs + (line.EndMs!.Value - line.StartMs!.Value), duration);
        }

        return Math.Min(newStartMs + FallbackLineSpanMs, duration);
    }

    /// <summary>
    /// Re-spread a line's words across a new span.
    ///
    /// <para>
    /// Proportionally when the line already had a real span, so the internal rhythm the aligner
    /// found is preserved and only the placement moves. When it did not — a line the aligner never
    /// managed to place, or a zero-length one — fall back to sharing the span out <b>by word
    /// length</b> rather than evenly: "I" and "unbelievable" plainly do not take the same time to
    /// sing, and an even split makes every long word late.
    /// </para>
    /// </summary>
    private static void RescaleLine(LyricsTimedLine line, long newStartMs, long newEndMs)
    {
        if (newEndMs < newStartMs)
        {
            newEndMs = newStartMs;
        }

        var newSpan = newEndMs - newStartMs;
        var timedWords = line.Words.Where(word => word.IsTimed).ToList();

        var oldStart = line.StartMs;
        var oldEnd = line.EndMs;
        var oldSpan = oldStart.HasValue && oldEnd.HasValue ? oldEnd.Value - oldStart.Value : 0;

        line.StartMs = newStartMs;
        line.EndMs = newEndMs;

        if (line.Words.Count == 0)
        {
            return;
        }

        if (timedWords.Count == line.Words.Count && oldSpan > 0 && oldStart.HasValue)
        {
            var scale = newSpan / (double)oldSpan;

            foreach (var word in line.Words)
            {
                var offsetStart = word.StartMs!.Value - oldStart.Value;
                var offsetEnd = word.EndMs!.Value - oldStart.Value;

                word.StartMs = newStartMs + (long)Math.Round(offsetStart * scale);
                word.EndMs = newStartMs + (long)Math.Round(offsetEnd * scale);
            }

            return;
        }

        DistributeByWordLength(line, newStartMs, newSpan);
    }

    private static void DistributeByWordLength(LyricsTimedLine line, long startMs, long spanMs)
    {
        var weights = line.Words
            .Select(word => (double)Math.Max(1, word.Text?.Trim().Length ?? 1))
            .ToList();

        var total = weights.Sum();
        if (total <= 0)
        {
            total = line.Words.Count;
            weights = line.Words.Select(_ => 1d).ToList();
        }

        var cursor = (double)startMs;

        for (var index = 0; index < line.Words.Count; index++)
        {
            var share = spanMs * (weights[index] / total);

            line.Words[index].StartMs = (long)Math.Round(cursor);
            cursor += share;
            line.Words[index].EndMs = (long)Math.Round(cursor);
        }
    }

    /// <summary>Close off the most recent timed line before <paramref name="lineIndex"/>.</summary>
    private static void EndPreviousLineAt(LyricsTimingsDocument document, int lineIndex, long boundaryMs)
    {
        for (var index = lineIndex - 1; index >= 0; index--)
        {
            var previous = document.Lines[index];
            if (!previous.IsTimed)
            {
                continue;
            }

            if (previous.EndMs <= boundaryMs)
            {
                return;
            }

            // Only the end moves. Re-spreading the words would move the previous line's internal
            // rhythm on the strength of a tap that said nothing about it.
            var newEnd = Math.Max(previous.StartMs!.Value, boundaryMs);
            previous.EndMs = newEnd;

            foreach (var word in previous.Words.Where(word => word.IsTimed && word.EndMs > newEnd))
            {
                word.EndMs = newEnd;
                if (word.StartMs > newEnd)
                {
                    word.StartMs = newEnd;
                }
            }

            return;
        }
    }
}
