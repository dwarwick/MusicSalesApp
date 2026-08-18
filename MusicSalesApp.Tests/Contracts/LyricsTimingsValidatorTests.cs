using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Tests.Contracts;

/// <summary>
/// The two strengths of rule, and why they are not the same rule.
///
/// <para>
/// <c>Normalize</c> runs after every edit and is not allowed to refuse anything: a creator halfway
/// through re-tapping a chorus has a document that briefly contradicts itself, and that is what the
/// middle of an edit looks like.  <c>Validate</c> runs once, at Publish, and is the last thing
/// standing between a mis-edited document and a karaoke display that drifts.
/// </para>
/// </summary>
[TestFixture]
public class LyricsTimingsValidatorTests
{
    private static LyricsTimingsDocument Document(params LyricsTimedLine[] lines) =>
        new() { SongId = 1, DurationMs = 100_000, Confidence = 0.8, Lines = lines.ToList() };

    private static LyricsTimedLine Line(string text, long? start, long? end, params (string Text, long? Start, long? End)[] words) =>
        new()
        {
            Text = text,
            StartMs = start,
            EndMs = end,
            Words = words
                .Select(word => new LyricsTimedWord { Text = word.Text, StartMs = word.Start, EndMs = word.End })
                .ToList()
        };

    public class Normalizing : LyricsTimingsValidatorTests
    {
        [Test]
        public void ATimeBeyondTheEndOfTheSongIsPulledBack()
        {
            var document = Document(Line("late", 99_000, 250_000, ("late", 99_000L, 250_000L)));

            LyricsTimingsValidator.Normalize(document);

            Assert.Multiple(() =>
            {
                Assert.That(document.Lines[0].EndMs, Is.EqualTo(100_000));
                Assert.That(document.Lines[0].Words[0].EndMs, Is.EqualTo(100_000));
            });
        }

        [Test]
        public void ANegativeTimeIsPulledUpToZero()
        {
            // Reachable by nudging the first word of a song earlier a few times.
            var document = Document(Line("early", -500, 900, ("early", -500L, 900L)));

            LyricsTimingsValidator.Normalize(document);

            Assert.That(document.Lines[0].StartMs, Is.EqualTo(0));
        }

        [Test]
        public void AWordEndingBeforeItStartsBecomesZeroLengthRatherThanNegative()
        {
            var document = Document(Line("x", 1_000, 2_000, ("x", 1_800L, 1_200L)));

            LyricsTimingsValidator.Normalize(document);

            Assert.That(document.Lines[0].Words[0].EndMs, Is.EqualTo(document.Lines[0].Words[0].StartMs));
        }

        [Test]
        public void ALineWidensToHoldAWordNudgedOutsideIt()
        {
            // The word is what the creator just placed deliberately, so the line yields, not the word.
            var document = Document(Line("x", 1_000, 2_000, ("x", 900L, 2_400L)));

            LyricsTimingsValidator.Normalize(document);

            Assert.Multiple(() =>
            {
                Assert.That(document.Lines[0].StartMs, Is.EqualTo(900));
                Assert.That(document.Lines[0].EndMs, Is.EqualTo(2_400));
                Assert.That(document.Lines[0].Words[0].StartMs, Is.EqualTo(900), "The word must not be clipped.");
            });
        }

        [Test]
        public void AnUntimedLineCannotKeepTimedWords()
        {
            // This combination is exactly what makes a section marker light up during the intro.
            var document = Document(Line("[Chorus]", null, null, ("[Chorus]", 5_000L, 6_000L)));

            LyricsTimingsValidator.Normalize(document);

            Assert.That(document.Lines[0].Words[0].IsTimed, Is.False);
        }

        [Test]
        public void NormalizingNeverThrowsOnAContradictoryDocument()
        {
            var document = Document(
                Line("b", 5_000, 1_000, ("b", 9_000L, -3L)),
                Line("a", -10, 500_000));

            Assert.DoesNotThrow(() => LyricsTimingsValidator.Normalize(document));
        }
    }

    public class Validating : LyricsTimingsValidatorTests
    {
        [Test]
        public void ACleanDocumentHasNothingWrongWithIt()
        {
            var document = Document(
                Line("Verse 1", null, null),
                Line("first", 1_000, 2_000, ("first", 1_000L, 2_000L)),
                Line("second", 2_500, 3_500, ("second", 2_500L, 3_500L)));

            Assert.That(LyricsTimingsValidator.Validate(document), Is.Empty);
        }

        [Test]
        public void OverlappingLinesAreRefused()
        {
            // Two lines highlighting at once reads to a listener as the song skipping.
            var document = Document(
                Line("first", 1_000, 5_000, ("first", 1_000L, 5_000L)),
                Line("second", 3_000, 6_000, ("second", 3_000L, 6_000L)));

            Assert.That(LyricsTimingsValidator.Validate(document), Has.Some.Contains("before"));
        }

        [Test]
        public void AWordOutsideItsOwnLineIsRefused()
        {
            var document = new LyricsTimingsDocument
            {
                DurationMs = 100_000,
                Lines = [Line("x", 1_000, 2_000)]
            };
            document.Lines[0].Words.Add(new LyricsTimedWord { Text = "stray", StartMs = 8_000, EndMs = 9_000 });

            Assert.That(LyricsTimingsValidator.Validate(document), Has.Some.Contains("outside"));
        }

        [Test]
        public void ATimingPastTheEndOfTheSongIsRefused()
        {
            var document = Document(Line("x", 99_000, 130_000, ("x", 99_000L, 130_000L)));

            Assert.That(LyricsTimingsValidator.Validate(document), Has.Some.Contains("past the end"));
        }

        [Test]
        public void ASmallOvershootIsToleratedBecauseTheDurationAndTheStemDisagreeSlightly()
        {
            // The duration is measured from the decode and the aligner works from a reassembled
            // stem; a few tens of milliseconds at the very end of a track is normal.
            var document = Document(Line("x", 99_000, 101_000, ("x", 99_000L, 101_000L)));

            Assert.That(LyricsTimingsValidator.Validate(document), Is.Empty);
        }

        [Test]
        public void TheToleranceMatchesThePipelinesOwnStructuralGate()
        {
            // A document the pipeline accepted must not be one the editor then refuses to publish.
            Assert.That(LyricsTimingsValidator.DurationOvershootTolerance, Is.EqualTo(1.05));
        }

        [Test]
        public void ADocumentWithNoTimingsAtAllIsRefused()
        {
            var document = Document(Line("[Chorus]", null, null), Line("", null, null));

            Assert.That(LyricsTimingsValidator.Validate(document), Has.Some.Contains("time against"));
        }

        [Test]
        public void AHalfTimedLineIsRefused()
        {
            // Alongside a good line, because a document whose *only* line is half-timed has no
            // usable timings at all and is refused by the earlier, blunter guard - which is correct,
            // just less specific. This is the realistic shape: one bad line among good ones.
            var document = Document(
                Line("good", 1_000, 2_000, ("good", 1_000L, 2_000L)),
                Line("half", 3_000, null));

            Assert.That(LyricsTimingsValidator.Validate(document), Has.Some.Contains("half a time"));
        }

        [Test]
        public void ProblemsAreWordedForACreatorNotAnEngineer()
        {
            // Rendered straight into the editor when Publish is refused. A creator told "timings are
            // not monotonic at index 47" has been told nothing at all.
            var document = Document(
                Line("Room went out of focus", 1_000, 5_000, ("Room", 1_000L, 5_000L)),
                Line("You were all I could see", 3_000, 6_000, ("You", 3_000L, 6_000L)));

            var problems = LyricsTimingsValidator.Validate(document);

            Assert.That(problems, Is.Not.Empty);
            foreach (var problem in problems)
            {
                Assert.That(problem.ToLowerInvariant(), Does.Not.Contain("monotonic"));
                Assert.That(problem.ToLowerInvariant(), Does.Not.Contain("millisecond"));
                Assert.That(problem.ToLowerInvariant(), Does.Not.Contain("startms"));
                Assert.That(problem, Does.Contain("\""), "The line should be quoted so it can be found.");
            }
        }

        [Test]
        public void SectionMarkersAreNotTreatedAsGapsBetweenLines()
        {
            // An untimed line between two sung ones must not break the monotonicity chain.
            var document = Document(
                Line("first", 1_000, 2_000, ("first", 1_000L, 2_000L)),
                Line("[Chorus]", null, null),
                Line("second", 2_500, 3_500, ("second", 2_500L, 3_500L)));

            Assert.That(LyricsTimingsValidator.Validate(document), Is.Empty);
        }
    }
}
