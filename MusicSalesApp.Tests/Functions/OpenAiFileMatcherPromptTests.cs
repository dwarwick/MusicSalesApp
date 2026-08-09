namespace MusicSalesApp.Tests.Functions;

/// <summary>
/// The instructions sent to the pairing model.
///
/// <para>
/// Not a unit test of behaviour - the behaviour lives in a model this repo does not run - but the
/// prompt is the only place a specific production failure was fixed, and it is a plain string that a
/// well-meaning edit can quietly undo. What is pinned here is the wording that failure turned on.
/// </para>
///
/// <para>
/// The failure: a batch of three songs and three images, two of them obviously paired. The prompt
/// opened with "match each audio file with the most similar image file", which reads as an assignment
/// problem, so the model handed the leftover image to the leftover song - pairing a headshot named
/// "david.JPG" with a track called "All Around Me" on no evidence at all. It published looking
/// entirely deliberate.
/// </para>
/// </summary>
[TestFixture]
public class OpenAiFileMatcherPromptTests
{
    /// <summary>
    /// The prompt with its line wrapping collapsed.
    ///
    /// <para>
    /// Asserted against the flattened form because the source wraps at 100 columns, so a phrase the
    /// model reads as one sentence is split by a newline and two spaces in the file. Matching the raw
    /// text would make these tests fail on a reflow that changed nothing the model sees.
    /// </para>
    /// </summary>
    private static string Prompt => System.Text.RegularExpressions.Regex.Replace(ReadSource(), @"\s+", " ");

    [Test]
    public void ThePromptDoesNotAskForAnAssignment()
    {
        // "Match each audio file with the most similar image" is the exact framing that caused it.
        // With N songs and N images there is always a "most similar" one left over.
        Assert.That(
            Prompt,
            Does.Not.Contain("Match each audio file with the most similar image"),
            "That framing makes leftovers into matches.");
    }

    [Test]
    public void ThePromptSaysMatchingNothingIsAcceptable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Prompt, Does.Contain("NOT an assignment problem"));
            Assert.That(Prompt, Does.Contain("Matching nothing is a perfectly good answer"));
        });
    }

    [Test]
    public void ThePromptForbidsMatchingByElimination()
    {
        // The specific reasoning step that produced the bad pair, named so the model cannot take it.
        Assert.Multiple(() =>
        {
            Assert.That(Prompt, Does.Contain("NEVER match by elimination"));
            Assert.That(
                Prompt,
                Does.Contain("not evidence that they belong together"),
                "Being the last two left must be explicitly disqualified as evidence.");
        });
    }

    [Test]
    public void ThePromptRequiresPositiveEvidence()
    {
        Assert.That(Prompt, Does.Contain("Only match on positive evidence"));
    }

    [Test]
    public void ThePromptSaysAWrongMatchIsWorseThanNoMatch()
    {
        // The asymmetry that justifies all of the above: an unmatched image costs the creator a drag
        // at the review step, and a confident wrong one looks correct and gets published.
        Assert.That(Prompt, Does.Contain("A wrong match is worse than no match"));
    }

    [Test]
    public void ThePromptStillRequiresTheIndexBasedContract()
    {
        // The parser bounds-checks indices and enforces the one-to-one claim itself, but it can only
        // do that if the model answers in indices at all.
        Assert.Multiple(() =>
        {
            Assert.That(Prompt, Does.Contain("numeric indices (NOT the filenames)"));
            Assert.That(Prompt, Does.Contain("unmatched_image_indices"));
            Assert.That(Prompt, Does.Contain("null image_index"));
            Assert.That(Prompt, Does.Contain("Each image file can match at most one audio file"));
        });
    }

    [Test]
    public void ThePromptStillHandlesTheMasteredSuffix()
    {
        // Every file in the creator's own library carries it, so losing this rule would unmatch
        // essentially every real batch.
        Assert.That(Prompt, Does.Contain("_mastered"));
    }

    private static string ReadSource()
        => File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "MusicSalesApp.Functions", "Matching", "OpenAiFileMatcher.cs"));

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MusicSalesApp", "MusicSalesApp.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
