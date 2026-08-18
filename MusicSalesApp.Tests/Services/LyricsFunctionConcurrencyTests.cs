using System.Text.Json;
using System.Text.RegularExpressions;

#nullable enable

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The one line of configuration standing between concurrent creators and an out-of-memory kill.
///
/// <para>
/// Demucs needs most of a 4096 MB Flex instance to separate one song — that limit is the platform's
/// ceiling, not a choice, since Flex offers only 512, 2048 and 4096 MB. Two separations sharing an
/// instance do not fit. <c>maxConcurrentActivityFunctions: 1</c> is what makes a second creator's
/// song scale <em>out</em> to a second instance rather than crowding onto the first.
/// </para>
///
/// <para>
/// <b>Why this is worth a test rather than a comment.</b> The failure it prevents is unusually
/// unforgiving: an out-of-memory kill arrives as <c>python exited with code 137</c> with no Python
/// traceback, and the orchestrator deliberately does not retry separation - a retry at the same
/// instance size fails the same way after spending the same minutes - so every crowded run fails
/// permanently. It would also only ever appear under concurrent load, which is exactly the condition
/// nobody reproduces while developing.
/// </para>
///
/// <para>
/// This reads the real <c>host.json</c> rather than restating its values, unlike
/// <see cref="LyricsTimeoutChainTests"/>, which pins numbers by transcription. Editing that file is
/// the way this protection would be lost, so the file is what the test has to look at.
/// </para>
/// </summary>
[TestFixture]
public class LyricsFunctionConcurrencyTests
{
    private static JsonElement DurableTaskSettings()
    {
        var path = FindHostJson();
        if (path is null)
        {
            Assert.Ignore("host.json is not reachable from this checkout.");
        }

        // The file is heavily commented using "//"-prefixed sibling keys, which is the house style
        // for JSON that has to stay valid. They are keys rather than comments, so this parses - the
        // strip is only to keep the assertion messages readable.
        var raw = File.ReadAllText(path!);
        var cleaned = Regex.Replace(raw, @"^\s*""//[^""]*"":\s*""(?:[^""\\]|\\.)*"",?\s*$", string.Empty, RegexOptions.Multiline);

        using var document = JsonDocument.Parse(cleaned);
        return document.RootElement
            .GetProperty("extensions")
            .GetProperty("durableTask")
            .Clone();
    }

    private static string? FindHostJson()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "MusicSalesApp.LyricsFunctions", "host.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    [Test]
    public void OnlyOneAlignmentRunsPerInstance()
    {
        var durable = DurableTaskSettings();

        Assert.That(
            durable.GetProperty("maxConcurrentActivityFunctions").GetInt32(),
            Is.EqualTo(1),
            "Demucs needs most of a 4 GB instance and Flex sells nothing larger, so a second "
            + "concurrent separation on the same instance is an out-of-memory kill that the "
            + "orchestrator will not retry. Raising this does not make the app faster - it makes "
            + "concurrent creators fail each other's songs.");
    }

    [Test]
    public void OrchestratorsAreNotHeldToTheSameLimit()
    {
        // Orchestrators are replay loops that dispatch and wait - they hold no audio and no model, so
        // throttling them to 1 as well would serialise the whole app for no benefit.
        var durable = DurableTaskSettings();

        Assert.That(
            durable.GetProperty("maxConcurrentOrchestratorFunctions").GetInt32(),
            Is.GreaterThan(1));
    }

    [Test]
    public void TheTaskHubIsStillPerEnvironment()
    {
        // Test and Production share a storage account, so a literal hub name here would put both
        // environments on one task hub, where they would steal each other's orchestrations.
        var durable = DurableTaskSettings();

        Assert.That(
            durable.GetProperty("hubName").GetString(),
            Does.StartWith("%").And.EndWith("%"),
            "The hub name must stay an app-setting reference, not a literal.");
    }
}
