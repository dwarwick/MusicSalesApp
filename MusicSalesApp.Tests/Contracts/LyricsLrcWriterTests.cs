using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Tests.Contracts;

/// <summary>
/// The C# LRC writer against the Python one it duplicates.
///
/// <para>
/// The expected strings below were not written by hand — they are the verbatim output of
/// <c>lyrics/formats.py::to_enhanced_lrc</c> run over the same input, captured while writing these
/// tests.  That is the only thing that makes duplicating a formatter across two languages defensible:
/// the alternative to duplicating it was re-running an eight-minute Demucs separation to regenerate a
/// file whose contents were already known.
/// </para>
/// </summary>
[TestFixture]
public class LyricsLrcWriterTests
{
    private static LyricsTimingsDocument Sample() => new()
    {
        SongId = 1,
        DurationMs = 700_000,
        Lines =
        [
            new LyricsTimedLine { Text = "Verse 1" },
            new LyricsTimedLine
            {
                Text = "Came home salty",
                StartMs = 10_465,
                EndMs = 11_685,
                Words =
                [
                    new LyricsTimedWord { Text = "Came", StartMs = 10_465, EndMs = 10_765 },
                    new LyricsTimedWord { Text = "home", StartMs = 10_765, EndMs = 11_025 },
                    new LyricsTimedWord { Text = "salty", StartMs = 11_025, EndMs = 11_685 }
                ]
            },
            new LyricsTimedLine { Text = "" },
            new LyricsTimedLine
            {
                Text = "Six months gone",
                StartMs = 613_226,
                EndMs = 614_546,
                Words =
                [
                    new LyricsTimedWord { Text = "Six", StartMs = 613_226, EndMs = 613_486 },
                    new LyricsTimedWord { Text = "months" },
                    new LyricsTimedWord { Text = "gone", StartMs = 613_926, EndMs = 614_546 }
                ]
            }
        ]
    };

    [Test]
    public void TheOutputMatchesThePythonWriterExactly()
    {
        const string expected =
            "[ti:Five Year Plan]\n"
            + "[ar:Dave]\n"
            + "Verse 1\n"
            + "[00:10.46]<00:10.46>Came <00:10.76>home <00:11.02>salty\n"
            + "\n"
            + "[10:13.22]<10:13.22>Six  months<10:13.92>gone\n";

        var actual = LyricsLrcWriter.Write(Sample(), title: "Five Year Plan", artist: "Dave");

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void AnUntimedWordKeepsThePythonWritersDoubleSpace()
    {
        // "Six  months" really does carry two spaces: a timed word emits a trailing space and an
        // untimed one a leading space. It looks like a bug and is not - matching the existing writer
        // byte for byte matters more than tidying it, because both files describe the same song and
        // any reader comparing them would see a difference that means nothing.
        var actual = LyricsLrcWriter.Write(Sample());

        Assert.That(actual, Does.Contain("Six  months"));
    }

    [Test]
    public void MinutesDoNotWrapAtSixty()
    {
        // A 75-minute mix is 75:00.00, not 15:00.00. Every LRC reader expects that, and it is the
        // only unambiguous reading.
        var document = new LyricsTimingsDocument
        {
            DurationMs = 5_000_000,
            Lines = [new LyricsTimedLine { Text = "late", StartMs = 4_500_000, EndMs = 4_501_000 }]
        };

        Assert.That(LyricsLrcWriter.Write(document), Does.Contain("[75:00.00]"));
    }

    [Test]
    public void AnUntimedLineIsEmittedWithoutATimestampRatherThanDropped()
    {
        // A reader that meets an untimed line simply shows it, which is exactly what a section
        // heading should do. Dropping them would change the lyrics the artist submitted.
        var actual = LyricsLrcWriter.Write(Sample());

        Assert.Multiple(() =>
        {
            Assert.That(actual, Does.Contain("Verse 1\n"));
            Assert.That(actual, Does.Not.Contain("]Verse 1"), "No timestamp may precede it.");
            Assert.That(actual, Does.Contain("salty\n\n"), "The blank line survives as a blank line.");
        });
    }

    [Test]
    public void TheTagsAreOmittedWhenThereIsNoTitleOrArtist()
    {
        var actual = LyricsLrcWriter.Write(Sample());

        Assert.Multiple(() =>
        {
            Assert.That(actual, Does.Not.Contain("[ti:"));
            Assert.That(actual, Does.Not.Contain("[ar:"));
            Assert.That(actual, Does.StartWith("Verse 1\n"));
        });
    }

    [Test]
    public void HundredthsTruncateRatherThanRound()
    {
        // Python does integer division by 10. Rounding here would put half the timestamps in this
        // file 10 ms away from the same timestamps in the JSON.
        var document = new LyricsTimingsDocument
        {
            DurationMs = 100_000,
            Lines = [new LyricsTimedLine { Text = "x", StartMs = 1_999, EndMs = 2_500 }]
        };

        Assert.That(LyricsLrcWriter.Write(document), Does.Contain("[00:01.99]"));
    }
}
