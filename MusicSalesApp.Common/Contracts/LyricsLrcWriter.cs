using System.Globalization;
using System.Text;

#nullable enable

namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// Writes Enhanced LRC from a timings document — the C# twin of
/// <c>MusicSalesApp.LyricsFunctions/lyrics/formats.py::to_enhanced_lrc</c>.
///
/// <para>
/// <b>It exists because publishing edited timings without it is a correctness bug, not a missing
/// nicety.</b> The creator's lyrics dialog offers a Download .lrc button, and that file is written
/// once by the Python side when an alignment lands. The moment a creator re-taps a chorus and
/// publishes, the JSON a listener sees and the LRC they can download describe different songs — and
/// nothing anywhere would say so. Regenerating the LRC at publish time keeps the two artifacts
/// describing the same thing.
/// </para>
///
/// <para>
/// Duplicating a formatter across two languages is a drift risk, and the alternative was worse: the
/// only other way to refresh the LRC would be to re-run the whole alignment - eight minutes of
/// separation on a 4 GB instance - to regenerate a file whose contents are already known.
/// <c>LyricsLrcWriterTests</c> pins the output against the Python writer's own format.
/// </para>
/// </summary>
public static class LyricsLrcWriter
{
    /// <summary>Render the document as Enhanced LRC, optionally carrying title and artist tags.</summary>
    public static string Write(LyricsTimingsDocument document, string? title = null, string? artist = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("[ti:").Append(title).Append("]\n");
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            builder.Append("[ar:").Append(artist).Append("]\n");
        }

        foreach (var line in document.Lines)
        {
            // An untimed line is emitted with no timestamp rather than dropped. A reader that meets
            // one simply shows it, which is exactly the behaviour wanted for a section heading.
            if (!line.StartMs.HasValue)
            {
                builder.Append(line.Text).Append('\n');
                continue;
            }

            var parts = new StringBuilder();
            parts.Append('[').Append(FormatTimestamp(line.StartMs.Value)).Append(']');

            foreach (var word in line.Words)
            {
                if (!word.StartMs.HasValue)
                {
                    parts.Append(' ').Append(word.Text);
                }
                else
                {
                    parts.Append('<').Append(FormatTimestamp(word.StartMs.Value)).Append('>')
                         .Append(word.Text).Append(' ');
                }
            }

            builder.Append(parts.ToString().TrimEnd()).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>mm:ss.xx</c> — the LRC convention, hundredths rather than milliseconds.
    /// </summary>
    /// <remarks>
    /// Minutes deliberately do not wrap at 60: a 75-minute mix is <c>75:00.00</c> rather than
    /// <c>15:00.00</c>, which is what every LRC reader expects and the only unambiguous reading.
    /// </remarks>
    private static string FormatTimestamp(long milliseconds)
    {
        if (milliseconds < 0)
        {
            milliseconds = 0;
        }

        var totalSeconds = milliseconds / 1000;
        var remainder = milliseconds % 1000;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        var hundredths = remainder / 10;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes:00}:{seconds:00}.{hundredths:00}");
    }
}
