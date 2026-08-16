using System.Text.Json;
using System.Text.Json.Nodes;
using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Tests.Contracts;

/// <summary>
/// The timings document is written by Python and, from now on, rewritten by C#.
///
/// <para>
/// That is a contract with no compiler behind it.  <c>MusicSalesApp.Common</c> is shared by
/// reference with the MAUI app, so a queue name or a step ordinal cannot drift between the two C#
/// halves - but the producer of this document is
/// <c>MusicSalesApp.LyricsFunctions/lyrics/formats.py</c>, in another language, deployed separately.
/// A field renamed on one side and not the other produces no error anywhere: the document simply
/// deserialises with that value missing, and a song's lyrics quietly lose their timings the first
/// time a creator saves.
/// </para>
///
/// <para>
/// So the fixture below is a verbatim copy of what <c>to_timing_json</c> emits, and the test asserts
/// the document survives a round trip through C# <em>unchanged as JSON</em> rather than merely
/// unchanged as objects.  Comparing the parsed trees catches what an object comparison cannot:
/// dropped nulls, renamed keys, and numbers that came back as decimals.
/// </para>
/// </summary>
[TestFixture]
public class LyricsTimingsRoundTripTests
{
    /// <summary>
    /// Mirrors <c>formats.py::to_timing_json</c>, and deliberately contains the four shapes most
    /// likely to be mishandled: an ordinary sung line, a section marker with null times and no
    /// words, a blank line, and a word whose end is exactly the next word's start.
    /// </summary>
    private const string Fixture = """
        {
          "songId": 42,
          "durationMs": 247360,
          "confidence": 0.5208,
          "lines": [
            {
              "text": "Verse 1",
              "startMs": null,
              "endMs": null,
              "words": []
            },
            {
              "text": "Came home salty",
              "startMs": 10465,
              "endMs": 11685,
              "words": [
                { "text": "Came", "startMs": 10465, "endMs": 10765 },
                { "text": "home", "startMs": 10765, "endMs": 11025 },
                { "text": "salty", "startMs": 11025, "endMs": 11685 }
              ]
            },
            {
              "text": "",
              "startMs": null,
              "endMs": null,
              "words": []
            },
            {
              "text": "Six months gone",
              "startMs": 13226,
              "endMs": 14546,
              "words": [
                { "text": "Six", "startMs": 13226, "endMs": 13486 },
                { "text": "months", "startMs": 13486, "endMs": 13926 },
                { "text": "gone", "startMs": 13926, "endMs": 14546 }
              ]
            }
          ]
        }
        """;

    [Test]
    public void TheDocumentSurvivesARoundTripAsJson()
    {
        var document = LyricsTimingsSerializer.Deserialize(Fixture);
        Assert.That(document, Is.Not.Null);

        var rewritten = LyricsTimingsSerializer.Serialize(document!);

        Assert.That(
            JsonNode.DeepEquals(JsonNode.Parse(Fixture), JsonNode.Parse(rewritten)),
            Is.True,
            $"The rewritten document differs from what Python wrote.\nExpected: {Fixture}\nActual: {rewritten}");
    }

    [Test]
    public void NullTimesAreWrittenRatherThanOmitted()
    {
        // Python writes "startMs": null for section markers and blank lines. If C# omitted the key
        // instead, this class would still round-trip its own output perfectly and would still be
        // wrong - the document would no longer match a freshly aligned one, and only a comparison
        // against the real writer's output would ever notice.
        var document = LyricsTimingsSerializer.Deserialize(Fixture)!;

        var rewritten = JsonNode.Parse(LyricsTimingsSerializer.Serialize(document))!;
        var marker = rewritten["lines"]!.AsArray()[0]!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(marker.ContainsKey("startMs"), Is.True, "startMs must be present.");
            Assert.That(marker["startMs"], Is.Null, "startMs must be null, not zero.");
            Assert.That(marker.ContainsKey("endMs"), Is.True, "endMs must be present.");
        });
    }

    [Test]
    public void AnUntimedLineDoesNotBecomeALineSungAtTheStartOfTheSong()
    {
        // The reason the times are `long?` rather than `long`. A non-nullable field turns every
        // [Chorus] and blank line into a line starting at 0:00, so the display lights up every
        // section heading during the intro and the monotonicity check then rejects the document.
        var document = LyricsTimingsSerializer.Deserialize(Fixture)!;

        var marker = document.Lines[0];

        Assert.Multiple(() =>
        {
            Assert.That(marker.StartMs, Is.Null);
            Assert.That(marker.IsTimed, Is.False);
            Assert.That(document.Lines[2].IsTimed, Is.False, "The blank line is untimed too.");
        });
    }

    [Test]
    public void KeysAreCamelCaseSoThePlayerCanReadWhatWeWrite()
    {
        var rewritten = JsonNode.Parse(LyricsTimingsSerializer.Serialize(new LyricsTimingsDocument
        {
            SongId = 1,
            DurationMs = 1000,
            Confidence = 0.5,
            Lines = [new LyricsTimedLine { Text = "x", StartMs = 0, EndMs = 10 }]
        }))!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(rewritten.ContainsKey("songId"), Is.True);
            Assert.That(rewritten.ContainsKey("durationMs"), Is.True);
            Assert.That(rewritten.ContainsKey("confidence"), Is.True);
            Assert.That(rewritten.ContainsKey("lines"), Is.True);
        });
    }

    [Test]
    public void ComputedPropertiesAreNotSerialised()
    {
        // IsTimed is a convenience for C# callers. Writing it into the document would add keys the
        // Python side has never heard of, and the round-trip assertion above is what would catch it.
        var rewritten = LyricsTimingsSerializer.Serialize(LyricsTimingsSerializer.Deserialize(Fixture)!);

        Assert.That(rewritten, Does.Not.Contain("isTimed").IgnoreCase);
    }

    [Test]
    public void AnUnreadableDocumentIsNullRatherThanAnException()
    {
        // Every caller is answering "can this song show lyrics", and for a truncated or corrupt blob
        // the answer is simply no. Throwing here would take out the song page.
        Assert.Multiple(() =>
        {
            Assert.That(LyricsTimingsSerializer.Deserialize("{ not json"), Is.Null);
            Assert.That(LyricsTimingsSerializer.Deserialize(""), Is.Null);
            Assert.That(LyricsTimingsSerializer.Deserialize("   "), Is.Null);
        });
    }

    [Test]
    public void TheSharedOptionsInstanceIsUsedByBothDirections()
    {
        // One instance, so a change to reading cannot silently fail to apply to writing.
        Assert.That(LyricsTimingsSerializer.Options.PropertyNamingPolicy, Is.EqualTo(JsonNamingPolicy.CamelCase));
        Assert.That(LyricsTimingsSerializer.Options.PropertyNameCaseInsensitive, Is.True);
    }
}
