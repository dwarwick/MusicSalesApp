using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Tests.Contracts;

/// <summary>
/// The arithmetic behind tap-along, which is what decides whether a creator re-timing a chorus gets
/// something usable or a pile of overlapping lines.
///
/// <para>
/// Worth testing exhaustively because it is the one part of the feature that can be wrong
/// <em>quietly</em>: every operation here produces a document that serialises, validates and renders
/// perfectly well while putting the words in the wrong places.
/// </para>
/// </summary>
[TestFixture]
public class LyricsTimingEditsTests
{
    private static LyricsTimingsDocument ThreeLines() => new()
    {
        SongId = 1,
        DurationMs = 100_000,
        Lines =
        [
            Line("one two", 1_000, 3_000, ("one", 1_000, 2_000), ("two", 2_000, 3_000)),
            Line("three four", 4_000, 6_000, ("three", 4_000, 5_000), ("four", 5_000, 6_000)),
            Line("five six", 7_000, 9_000, ("five", 7_000, 8_000), ("six", 8_000, 9_000))
        ]
    };

    private static LyricsTimedLine Line(string text, long? start, long? end, params (string Text, long Start, long End)[] words) =>
        new()
        {
            Text = text,
            StartMs = start,
            EndMs = end,
            Words = words
                .Select(word => new LyricsTimedWord { Text = word.Text, StartMs = word.Start, EndMs = word.End })
                .ToList()
        };

    public class Retiming : LyricsTimingEditsTests
    {
        [Test]
        public void TheLineMovesToTheTappedMoment()
        {
            var document = ThreeLines();

            LyricsTimingEdits.RetimeLine(document, 1, 4_500);

            Assert.That(document.Lines[1].StartMs, Is.EqualTo(4_500));
        }

        [Test]
        public void TheWordsKeepTheirRelativePositionsInsideTheLine()
        {
            // The aligner's guess at the internal rhythm of a line is usually good even when its
            // placement was wrong, so re-timing moves the line and preserves the shape inside it.
            var document = new LyricsTimingsDocument
            {
                DurationMs = 100_000,
                Lines = [Line("a b c", 1_000, 5_000, ("a", 1_000, 2_000), ("b", 2_000, 3_000), ("c", 3_000, 5_000))]
            };

            LyricsTimingEdits.RetimeLine(document, 0, 11_000);

            var words = document.Lines[0].Words;
            Assert.Multiple(() =>
            {
                Assert.That(words[0].StartMs, Is.EqualTo(11_000));
                Assert.That(words[1].StartMs, Is.EqualTo(12_000), "b sat a quarter of the way in.");
                Assert.That(words[2].StartMs, Is.EqualTo(13_000), "c sat halfway in.");
                Assert.That(words[2].EndMs, Is.EqualTo(15_000));
            });
        }

        [Test]
        public void TappingALineImplicitlyEndsThePreviousOne()
        {
            // The single most important behaviour here. A creator tapping in time is telling us where
            // line two begins, which is also the best information anyone has about where line one
            // stopped. Without this, a record pass leaves every earlier line still running underneath
            // and two lines highlight at once.
            var document = ThreeLines();

            LyricsTimingEdits.RetimeLine(document, 1, 2_000);

            Assert.That(document.Lines[0].EndMs, Is.EqualTo(2_000));
        }

        [Test]
        public void EndingThePreviousLineAlsoPullsBackItsOverhangingWords()
        {
            var document = ThreeLines();

            LyricsTimingEdits.RetimeLine(document, 1, 1_500);

            Assert.That(document.Lines[0].Words.Max(word => word.EndMs), Is.LessThanOrEqualTo(1_500));
        }

        [Test]
        public void APreviousLineThatAlreadyEndedInTimeIsLeftAlone()
        {
            var document = ThreeLines();
            var originalEnd = document.Lines[0].EndMs;

            LyricsTimingEdits.RetimeLine(document, 1, 8_000);

            Assert.That(document.Lines[0].EndMs, Is.EqualTo(originalEnd));
        }

        [Test]
        public void TheLineRunsUpToTheNextOneWhenThatIsAlreadyPlacedLater()
        {
            var document = ThreeLines();

            LyricsTimingEdits.RetimeLine(document, 1, 5_000);

            Assert.That(document.Lines[1].EndMs, Is.EqualTo(7_000), "It should meet line three's start.");
        }

        [Test]
        public void ALineTheAlignerNeverPlacedSharesItsSpanOutByWordLength()
        {
            // "I" and "unbelievable" plainly do not take the same time to sing. An even split makes
            // every long word late, which is exactly the case a creator is re-tapping to fix.
            var document = new LyricsTimingsDocument
            {
                DurationMs = 100_000,
                Lines =
                [
                    new LyricsTimedLine
                    {
                        Text = "I unbelievable",
                        Words =
                        [
                            new LyricsTimedWord { Text = "I" },
                            new LyricsTimedWord { Text = "unbelievable" }
                        ]
                    }
                ]
            };

            LyricsTimingEdits.RetimeLine(document, 0, 10_000);

            var words = document.Lines[0].Words;
            var shortWord = words[0].EndMs!.Value - words[0].StartMs!.Value;
            var longWord = words[1].EndMs!.Value - words[1].StartMs!.Value;

            Assert.That(longWord, Is.GreaterThan(shortWord * 5), "'unbelievable' is 12 letters against 1.");
        }

        [Test]
        public void ARecordPassDownTheWholeSongLeavesNoOverlaps()
        {
            // The end-to-end property: tap every line in order, then the document must publish.
            var document = ThreeLines();

            LyricsTimingEdits.RetimeLine(document, 0, 2_000);
            LyricsTimingEdits.RetimeLine(document, 1, 5_000);
            LyricsTimingEdits.RetimeLine(document, 2, 9_000);

            Assert.That(LyricsTimingsValidator.Validate(document), Is.Empty);
        }

        [Test]
        public void ATapPastTheEndOfTheSongIsClamped()
        {
            var document = ThreeLines();

            LyricsTimingEdits.RetimeLine(document, 2, 500_000);

            Assert.That(document.Lines[2].EndMs, Is.LessThanOrEqualTo(100_000));
        }

        [Test]
        public void AnOutOfRangeLineIndexDoesNothingRatherThanThrowing()
        {
            var document = ThreeLines();

            Assert.Multiple(() =>
            {
                Assert.DoesNotThrow(() => LyricsTimingEdits.RetimeLine(document, 99, 1_000));
                Assert.DoesNotThrow(() => LyricsTimingEdits.RetimeLine(document, -1, 1_000));
            });
        }
    }

    public class Nudging : LyricsTimingEditsTests
    {
        [Test]
        public void AWordMovesWithoutChangingLength()
        {
            // Nudging "focus" means it is sung slightly later, not that it is sung for longer.
            var document = ThreeLines();
            var before = document.Lines[0].Words[1];
            var length = before.EndMs!.Value - before.StartMs!.Value;

            LyricsTimingEdits.NudgeWord(document, 0, 1, 50);

            var after = document.Lines[0].Words[1];
            Assert.Multiple(() =>
            {
                Assert.That(after.StartMs, Is.EqualTo(2_050));
                Assert.That(after.EndMs!.Value - after.StartMs!.Value, Is.EqualTo(length));
            });
        }

        [Test]
        public void NudgingAWordOutOfItsLineWidensTheLineRatherThanClippingTheWord()
        {
            var document = ThreeLines();

            LyricsTimingEdits.NudgeWord(document, 0, 0, -500);

            Assert.Multiple(() =>
            {
                Assert.That(document.Lines[0].Words[0].StartMs, Is.EqualTo(500));
                Assert.That(document.Lines[0].StartMs, Is.EqualTo(500));
            });
        }

        [Test]
        public void NudgingAWholeLineMovesItsWordsWithIt()
        {
            var document = ThreeLines();

            LyricsTimingEdits.NudgeLine(document, 0, 250);

            Assert.Multiple(() =>
            {
                Assert.That(document.Lines[0].StartMs, Is.EqualTo(1_250));
                Assert.That(document.Lines[0].Words[0].StartMs, Is.EqualTo(1_250));
                Assert.That(document.Lines[0].Words[1].StartMs, Is.EqualTo(2_250));
            });
        }

        [Test]
        public void AnUntimedLineCannotBeNudged()
        {
            var document = new LyricsTimingsDocument
            {
                DurationMs = 100_000,
                Lines = [Line("[Chorus]", null, null)]
            };

            LyricsTimingEdits.NudgeLine(document, 0, 500);

            Assert.That(document.Lines[0].IsTimed, Is.False);
        }
    }

    public class Copying : LyricsTimingEditsTests
    {
        [Test]
        public void ACopiedLineDoesNotShareWordsWithTheOriginal()
        {
            // The undo stack holds these. Sharing word objects would make every undo a no-op.
            var document = ThreeLines();
            var snapshot = LyricsTimingEdits.CopyLine(document.Lines[0]);

            LyricsTimingEdits.NudgeWord(document, 0, 0, 5_000);

            Assert.That(snapshot.Words[0].StartMs, Is.EqualTo(1_000));
        }

        [Test]
        public void ACopiedDocumentIsFullyIndependent()
        {
            // "Reset everything" reads from this. A shallow copy would reset to the edited state.
            var document = ThreeLines();
            var pristine = LyricsTimingEdits.CopyDocument(document);

            LyricsTimingEdits.RetimeLine(document, 1, 20_000);

            Assert.That(pristine.Lines[1].StartMs, Is.EqualTo(4_000));
        }
    }
}
