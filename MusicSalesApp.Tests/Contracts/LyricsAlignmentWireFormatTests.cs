using System.Text.Json;
using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Tests.Contracts;

/// <summary>
/// The wire format between the Python Function app and this one.
///
/// <para>
/// Every other contract in <c>MusicSalesApp.Common</c> is exchanged between two C# processes, where
/// both ends share the compiled type and the shape cannot disagree. This one crosses a language
/// boundary, which removes that guarantee entirely - and the failure it removes it into is
/// particularly nasty, because it only bites on the <em>success</em> path: a run that worked, that
/// spent tens of minutes of CPU, whose result is rejected at the final hop and recorded as a
/// failure.
/// </para>
/// </summary>
[TestFixture]
public class LyricsAlignmentWireFormatTests
{
    /// <summary>Matches what ASP.NET Core's model binding and the reconciler both use.</summary>
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void TheOutcomeArrivesAsAName()
    {
        // Python has no C# enum to number, so it sends "Aligned". JsonSerializerDefaults.Web does
        // NOT accept a name unless the type says so - which is why LyricsAlignmentOutcome carries a
        // JsonStringEnumConverter. Remove that attribute and this is the test that fails.
        var json = """{"jobId":"0f9c1d2e-3a4b-4c5d-6e7f-8a9b0c1d2e3f","outcome":"Aligned"}""";

        var result = JsonSerializer.Deserialize<LyricsAlignmentResult>(json, WebOptions);

        Assert.That(result!.Outcome, Is.EqualTo(LyricsAlignmentOutcome.Aligned));
    }

    [TestCase("Aligned", LyricsAlignmentOutcome.Aligned)]
    [TestCase("Unusable", LyricsAlignmentOutcome.Unusable)]
    [TestCase("Inconclusive", LyricsAlignmentOutcome.Inconclusive)]
    public void EveryOutcomeNameRoundTrips(string name, LyricsAlignmentOutcome expected)
    {
        var json = $$"""{"outcome":"{{name}}"}""";

        var result = JsonSerializer.Deserialize<LyricsAlignmentResult>(json, WebOptions);

        Assert.That(result!.Outcome, Is.EqualTo(expected));
    }

    [Test]
    public void TheStepStaysNumeric()
    {
        // Deliberately the opposite choice from the outcome. The step's entire contract is that its
        // ordinals are compared with ">" to decide whether a progress ping is an advance, so the
        // number is the meaningful thing and the Python side sends int(step).
        var json = """{"jobId":"0f9c1d2e-3a4b-4c5d-6e7f-8a9b0c1d2e3f","step":30,"overallPercent":15.0}""";

        var progress = JsonSerializer.Deserialize<LyricsAlignmentProgress>(json, WebOptions);

        Assert.That(progress!.Step, Is.EqualTo(LyricsAlignmentStep.SeparatingVocals));
    }

    [Test]
    public void TheFullSuccessPayloadBindsAsThePythonAppSendsIt()
    {
        // Field for field, camelCased, exactly as function_app.py builds it. If a property is
        // renamed on either side, this is what notices.
        var json = """
        {
          "jobId": "0f9c1d2e-3a4b-4c5d-6e7f-8a9b0c1d2e3f",
          "outcome": "Aligned",
          "timingsBlobPath": "lyrics/0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f/timings.json",
          "lrcBlobPath": "lyrics/0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f/lyrics.lrc",
          "confidence": 0.8731,
          "meanAlignerConfidence": 0.9012,
          "lyricTokenCount": 300,
          "matchedTokenCount": 281,
          "interpolatedTokenCount": 19,
          "droppedAlignerTokenCount": 4,
          "silenceViolationCount": 2,
          "lineCount": 40,
          "linesWithTimingCount": 40,
          "isMonotonic": true,
          "lastWordEndMs": 210400,
          "durationMs": 214000
        }
        """;

        var result = JsonSerializer.Deserialize<LyricsAlignmentResult>(json, WebOptions);

        Assert.Multiple(() =>
        {
            Assert.That(result!.Outcome, Is.EqualTo(LyricsAlignmentOutcome.Aligned));
            Assert.That(result.Confidence, Is.EqualTo(0.8731).Within(0.0001));
            Assert.That(result.MatchedTokenCount, Is.EqualTo(281));
            Assert.That(result.LinesWithTimingCount, Is.EqualTo(40));
            Assert.That(result.IsMonotonic, Is.True);
            Assert.That(result.DurationMs, Is.EqualTo(214_000));
            Assert.That(result.TimingsBlobPath, Does.StartWith("lyrics/"));
        });
    }

    [Test]
    public void AFailurePayloadBindsWithoutTheSuccessOnlyFields()
    {
        // The orchestrator's except path sends a much smaller object. Anything it omits has to be
        // optional, or a failed run cannot even report that it failed.
        var json = """
        {
          "jobId": "0f9c1d2e-3a4b-4c5d-6e7f-8a9b0c1d2e3f",
          "outcome": "Inconclusive",
          "failureCode": "SeparationFailed",
          "diagnostic": "Demucs failed (exit 137)",
          "isMonotonic": true
        }
        """;

        var result = JsonSerializer.Deserialize<LyricsAlignmentResult>(json, WebOptions);

        Assert.Multiple(() =>
        {
            Assert.That(result!.Outcome, Is.EqualTo(LyricsAlignmentOutcome.Inconclusive));
            Assert.That(result.FailureCode, Is.EqualTo("SeparationFailed"));
            Assert.That(result.TimingsBlobPath, Is.Null);
            Assert.That(result.Confidence, Is.Null);
        });
    }
}
