#nullable enable
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The scrolling lyrics panel.
///
/// <para>
/// Only the markup and the handover to JS are testable here, and that is the right split rather than
/// a limitation: the highlighting deliberately runs in a <c>requestAnimationFrame</c> loop against
/// the audio element's clock, because doing it in C# would mean a SignalR round trip per frame on a
/// Server circuit. What C# is responsible for is rendering every word with the attributes the loop
/// needs, keeping untimed lines out of the timing stream, and handing over a flattened payload.
/// </para>
/// </summary>
[TestFixture]
public class LyricsScrollerTests : BUnitTestBase
{
    private Mock<IJSObjectReference> _module = null!;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        var js = new Mock<IJSRuntime>();
        _module = new Mock<IJSObjectReference>();
        js.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .ReturnsAsync(_module.Object);

        TestContext.Services.AddSingleton(js.Object);
        SetupRendererInfo();
    }

    private static LyricsTimingsDocument Document() => new()
    {
        SongId = 1,
        DurationMs = 240_000,
        Lines =
        [
            new LyricsTimedLine { Text = "[Chorus]" },
            new LyricsTimedLine
            {
                Text = "one two",
                StartMs = 1_000,
                EndMs = 3_000,
                Words =
                [
                    new LyricsTimedWord { Text = "one", StartMs = 1_000, EndMs = 2_000 },
                    new LyricsTimedWord { Text = "two", StartMs = 2_000, EndMs = 3_000 }
                ]
            },
            new LyricsTimedLine { Text = "" }
        ]
    };

    private IRenderedComponent<LyricsScroller> Render(
        LyricsTimingsDocument? document,
        LyricsScrollerMode mode = LyricsScrollerMode.Player)
        => TestContext.Render<LyricsScroller>(pb => pb
            .Add(p => p.Document, document)
            .Add(p => p.Mode, mode));

    [Test]
    public void EveryTimedWordCarriesTheAttributesTheLoopNeeds()
    {
        // data-l and data-w are how the JS loop finds the element to highlight, and how a click in
        // the editor reports which word was hit. Without them the panel renders and never lights up.
        var cut = Render(Document());

        var words = cut.FindAll("[data-w]");

        Assert.Multiple(() =>
        {
            Assert.That(words, Has.Count.EqualTo(2));
            Assert.That(words[0].GetAttribute("data-l"), Is.EqualTo("1"));
            Assert.That(words[0].GetAttribute("data-w"), Is.EqualTo("0"));
            Assert.That(words[1].GetAttribute("data-w"), Is.EqualTo("1"));
        });
    }

    [Test]
    public void SectionMarkersAndBlanksRenderButAreNotTimeable()
    {
        // They are part of how the artist laid the song out, so they are shown. They have no time, so
        // they must never be highlighted or scrolled to - which is what the missing data-l enforces.
        var cut = Render(Document());

        var untimed = cut.FindAll(".lyrics-line--untimed");

        Assert.Multiple(() =>
        {
            Assert.That(untimed, Has.Count.EqualTo(2), "The [Chorus] marker and the blank line.");
            Assert.That(cut.Markup, Does.Contain("[Chorus]"));
            Assert.That(untimed[0].GetAttribute("data-l"), Is.Null);
        });
    }

    [Test]
    public void TheArtistsOwnCapitalisationAndPunctuationSurvive()
    {
        // Normalisation is for matching only. What is displayed is what was submitted.
        var document = new LyricsTimingsDocument
        {
            DurationMs = 10_000,
            Lines =
            [
                new LyricsTimedLine
                {
                    Text = "I don't do timelines like that",
                    StartMs = 0,
                    EndMs = 2_000,
                    Words = [new LyricsTimedWord { Text = "I don't", StartMs = 0, EndMs = 2_000 }]
                }
            ]
        };

        Assert.That(Render(document).Markup, Does.Contain("I don't"));
    }

    [TestCase(LyricsScrollerMode.Card, "lyrics-scroller--card")]
    [TestCase(LyricsScrollerMode.Player, "lyrics-scroller--player")]
    [TestCase(LyricsScrollerMode.Editor, "lyrics-scroller--editor")]
    public void TheModeIsExpressedAsAClassRatherThanDifferentMarkup(
        LyricsScrollerMode mode,
        string expected)
    {
        // One component, three sizes. If the modes ever diverge into different markup, the card and
        // the player stop being the same thing and drift apart.
        Assert.That(Render(Document(), mode).Markup, Does.Contain(expected));
    }

    [Test]
    public void WithNoTimingsItSaysSoRatherThanRenderingAnEmptyBox()
    {
        var cut = Render(document: null);

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".lyrics-scroller-empty"), Is.Not.Empty);
            Assert.That(cut.FindAll("[data-w]"), Is.Empty);
        });
    }

    [Test]
    public void ALineWhoseWordsCouldNotBePlacedStillHighlightsAsAWhole()
    {
        // The aligner sometimes places a line but not the words inside it. Dropping the line would
        // lose lyrics the artist submitted; showing it un-highlightable would look broken.
        var document = new LyricsTimingsDocument
        {
            DurationMs = 10_000,
            Lines = [new LyricsTimedLine { Text = "whole line only", StartMs = 500, EndMs = 2_500 }]
        };

        var cut = Render(document);

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".lyrics-line[data-l]"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Contain("whole line only"));
        });
    }

    // -----------------------------------------------------------------
    // The payload handed to the loop
    // -----------------------------------------------------------------

    [Test]
    public void FlattenEmitsOneAscendingEntryPerTimedWord()
    {
        // The loop does a lookup every animation frame, so it wants one ascending array to walk or
        // binary-search, not a tree to descend.
        var flat = LyricsScrollerModel.Flatten(Document());

        Assert.Multiple(() =>
        {
            Assert.That(flat.Starts, Is.EqualTo(new long[] { 1_000, 2_000 }));
            Assert.That(flat.Ends, Is.EqualTo(new long[] { 2_000, 3_000 }));
            Assert.That(flat.LineOf, Is.EqualTo(new[] { 1, 1 }));
            Assert.That(flat.WordOf, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(flat.DurationMs, Is.EqualTo(240_000));
        });
    }

    [Test]
    public void FlattenLeavesUntimedLinesOutOfTheTimingStreamEntirely()
    {
        // A section marker with a null start would deserialise elsewhere as 0 and light up during the
        // intro. Keeping it out of the flattened arrays is what makes that impossible here.
        var flat = LyricsScrollerModel.Flatten(Document());

        Assert.That(flat.LineOf, Has.None.EqualTo(0).And.None.EqualTo(2));
    }

    [Test]
    public void FlattenGivesAWordlessLineASingleSpanCoveringIt()
    {
        var document = new LyricsTimingsDocument
        {
            DurationMs = 10_000,
            Lines = [new LyricsTimedLine { Text = "whole line", StartMs = 500, EndMs = 2_500 }]
        };

        var flat = LyricsScrollerModel.Flatten(document);

        Assert.Multiple(() =>
        {
            Assert.That(flat.Starts, Is.EqualTo(new long[] { 500 }));
            Assert.That(flat.Ends, Is.EqualTo(new long[] { 2_500 }));
        });
    }

    [Test]
    public void FlattenSkipsAWordTheAlignerCouldNotPlace()
    {
        var document = new LyricsTimingsDocument
        {
            DurationMs = 10_000,
            Lines =
            [
                new LyricsTimedLine
                {
                    Text = "a b",
                    StartMs = 0,
                    EndMs = 2_000,
                    Words =
                    [
                        new LyricsTimedWord { Text = "a", StartMs = 0, EndMs = 1_000 },
                        new LyricsTimedWord { Text = "b" }
                    ]
                }
            ]
        };

        Assert.That(LyricsScrollerModel.Flatten(document).Starts, Has.Length.EqualTo(1));
    }
}
