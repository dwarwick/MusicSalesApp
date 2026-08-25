using System.Text.RegularExpressions;

namespace MusicSalesApp.Tests.Components;

/// <summary>
/// The upload page used to carry a 178-line inline &lt;style&gt; block: twelve hard-coded colours,
/// no dark variant for any of them, and sizing that no breakpoint sheet could reach. These guard
/// the state it was moved into, because the block grew a line at a time and would again.
/// </summary>
[TestFixture]
public class UploadFilesStyleRoutingTests
{
    private static string Razor() =>
        ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor");

    [Test]
    public void TheUploadPageCarriesNoStyleBlock()
    {
        // Colour belongs in light.css / dark.css, layout in app.css, sizing in the breakpoint
        // sheets. A <style> block in a .razor can hold all three and be themed for none of them.
        Assert.That(Razor(), Does.Not.Contain("<style>"));
    }

    [Test]
    public void TheUploadPageHardCodesNoColours()
    {
        var hex = Regex.Matches(Razor(), @"#[0-9a-fA-F]{3,8}\b")
            .Select(m => m.Value)
            .ToList();

        Assert.That(hex, Is.Empty,
            "a literal colour here cannot follow the theme; every one of these has a --st-* token");
    }

    [Test]
    public void TheUploadPageUsesNoGlyphIconFont()
    {
        var markup = Razor();

        Assert.Multiple(() =>
        {
            // Inline SVG with fill="currentColor" is the redesign standard, for the plain reason
            // that a glyph font ignores currentColor and so cannot be themed.
            Assert.That(markup, Does.Not.Contain("fa-solid"));
            Assert.That(markup, Does.Not.Contain("fas fa-"));
            Assert.That(markup, Does.Contain("fill=\"currentColor\""));
        });
    }

    [Test]
    public void TheReviewTableIsLabelledSoItCanBecomeCards()
    {
        // Below 992px the header row is hidden and each cell is titled from its data-label. A cell
        // without one becomes an unlabelled control on a phone, which is worse than a wide table.
        var markup = Razor();

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("review-table"));
            foreach (var label in new[] { "Song title", "Genre", "Artist persona", "AI disclosure", "Cover art" })
            {
                Assert.That(markup, Does.Contain($"data-label=\"{label}\""),
                    $"the {label} cell needs a label to survive the card treatment");
            }
        });
    }

    [Test]
    public void TheCardTreatmentIsOnTheReviewTableNotTheReceivingOne()
    {
        // This page has two tables with identical opening markup - the "Receiving Files" one and
        // the review one - and the class landed on the wrong one, so the card layout never applied
        // to anything a creator was looking at. Anchoring on the header is the only way to tell
        // them apart from the source.
        var markup = Razor();

        var classAt = markup.IndexOf("review-table", StringComparison.Ordinal);
        Assert.That(classAt, Is.GreaterThanOrEqualTo(0), "the review table needs the class");

        var firstHeader = markup.IndexOf("<th>", classAt, StringComparison.Ordinal);
        var header = markup.Substring(firstHeader, 40);

        Assert.That(header, Does.Contain("Song Title"),
            "the class is on whichever table comes next, and that has to be the one with the metadata columns");
    }

    [Test]
    public void TheCardRulesOutrankBootstrap()
    {
        // Bootstrap styles cells at .table > :not(caption) > * > *, specificity 0,1,3, and sets
        // padding, a bottom border, a background and the stripe. A plain ".review-table td" is
        // 0,1,1 and silently loses all four, which is what the first version did.
        var md = ReadProjectFile("MusicSalesApp", "wwwroot", "md_app.css");

        Assert.Multiple(() =>
        {
            Assert.That(md, Does.Contain(".review-table > tbody > tr > td"),
                "the child-combinator form is what matches Bootstrap's specificity");
            Assert.That(md, Does.Contain("box-shadow: none !important"),
                "Bootstrap paints the stripe as an inset box-shadow, so clearing the background is not enough");
            Assert.That(md, Does.Contain("min-width: 0 !important"),
                "the cells carry inline min-widths that would keep the table wide");
        });
    }

    [Test]
    public void TheDropTargetIsNamedByShapeNotColour()
    {
        // It used to say "green dashed box". That pinned the CSS to a hue with no token behind it,
        // and left anyone who cannot separate green from grey without the one instruction that
        // mattered.
        var markup = Razor();

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Not.Contain("green dashed box"));
            Assert.That(markup, Does.Contain("dashed box"));
        });
    }

    [Test]
    public void ChoosingAPersonaIsNeverRequired()
    {
        // Explicitly not mandatory: most creators have none, and a song with no persona carries
        // the creator display name instead. Only the title and the genre gate an upload.
        var codeBehind = ReadProjectFile(
            "MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

        var gate = codeBehind[codeBehind.IndexOf("PendingBatchNeedsAttentionAsync", StringComparison.Ordinal)..];
        gate = gate[..gate.IndexOf("return true;", StringComparison.Ordinal)];

        Assert.That(gate, Does.Not.Contain("PersonaId"),
            "nothing about a persona may block a batch from uploading");
        Assert.That(gate, Does.Contain("GenreError"), "a genre still has to gate it");
        Assert.That(gate, Does.Contain("TitleConfirmed"), "so does an unchecked title");
    }

    private static string ReadProjectFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(GetRepositoryRoot(), Path.Combine(pathParts)));

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

        throw new DirectoryNotFoundException("Could not locate the repository root from the test directory.");
    }
}
