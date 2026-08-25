#nullable enable
using System.Text.RegularExpressions;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// Guards the split between --st-line and --st-control-line, and the solidity of --st-warn-tint.
///
/// Both are asserted against the stylesheets rather than rendered markup because both failures are
/// invisible in markup and invisible in a screenshot review: the control renders in exactly the
/// same place either way, and the only symptom is that its boundary drops under the 3:1 WCAG
/// 1.4.11 floor - or, for the tint, that a warning's legibility silently depends on which
/// background the alert happened to land on. The new border is deliberately heavier than the old
/// hairline; that is the rule, not a drawing error, so this stops it being tidied back.
/// </summary>
[TestFixture]
public class ControlBorderTokenCssTests
{
    /// <summary>Rules whose border is the boundary of something you click or type into.</summary>
    private static readonly string[] ControlRules =
    {
        ".action-button",
        ".filter-pill",
        ".filter-pill-search-input",
        ".card-mini-controls .e-btn:active",
        ".cta-outline",
    };

    /// <summary>Rules that are surfaces, not controls. WCAG 1.4.11 does not reach these.</summary>
    private static readonly string[] SurfaceRules =
    {
        ".e-card.music-card",
        ".filter-pill-dropdown",
        ".cta-card",
    };

    private static readonly string[] ThemeFiles = { "light.css", "dark.css" };

    private static string ReadThemeCss(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MusicSalesApp.slnx")))
            dir = dir.Parent;

        Assert.That(dir, Is.Not.Null, "could not locate the repository root from the test assembly");

        var cssPath = Path.Combine(dir!.FullName, "MusicSalesApp", "wwwroot", fileName);
        Assert.That(File.Exists(cssPath), Is.True, $"{fileName} not found at {cssPath}");

        return File.ReadAllText(cssPath);
    }

    /// <summary>
    /// Every rule body whose selector list mentions <paramref name="selector"/>, joined. A
    /// selector can appear in more than one rule - ".cta-card" heads a colour-only rule as well
    /// as the grouped ".feature-card, .cta-card" rule that carries the border - so matching only
    /// the first is what a naive version of this test got wrong.
    /// </summary>
    private static string ReadRuleBodies(string css, string selector, string fileName)
    {
        // The leading guard stops ".filter-pill" matching ".filter-pill-search-input"; the
        // trailing one allows the selector to sit anywhere in a comma-separated group.
        var matches = Regex.Matches(css, $@"(?<![\w.-]){Regex.Escape(selector)}(?![\w-])[^{{}}]*\{{([^}}]*)\}}");
        Assert.That(matches, Is.Not.Empty, $"no rule found for {selector} in {fileName}");
        return string.Join(Environment.NewLine, matches.Select(match => match.Groups[1].Value));
    }

    [Test]
    public void BothThemesDeclareTheControlBorderAndWarningTint()
    {
        foreach (var fileName in ThemeFiles)
        {
            var css = ReadThemeCss(fileName);

            Assert.Multiple(() =>
            {
                Assert.That(css, Does.Match(@"--st-control-line\s*:"),
                    $"{fileName} must declare --st-control-line; the pair is kept in step by hand");
                Assert.That(css, Does.Match(@"--st-warn-tint\s*:"),
                    $"{fileName} must declare --st-warn-tint");
            });
        }
    }

    [Test]
    public void TheWarningTintIsSolid()
    {
        foreach (var fileName in ThemeFiles)
        {
            var css = ReadThemeCss(fileName);
            var value = Regex.Match(css, @"--st-warn-tint\s*:\s*([^;]+);").Groups[1].Value.Trim();

            Assert.Multiple(() =>
            {
                // An alpha composites differently over --st-page than over --st-surface, so the
                // pair could not be measured once. Over --st-page the old alpha left --st-warn at
                // 4.34:1, under AA, which is exactly the bug this replaced.
                Assert.That(value, Does.Not.Contain("rgba"),
                    $"--st-warn-tint in {fileName} must be solid, not an alpha - see AGENTS.md");
                Assert.That(value, Does.StartWith("#"),
                    $"--st-warn-tint in {fileName} should be a measured hex value");
            });
        }
    }

    [Test]
    public void TheReplacedAlphaWarningTokenIsGone()
    {
        foreach (var fileName in ThemeFiles)
        {
            // Renamed rather than added beside, because it had no consumers. Re-introducing it
            // would quietly reopen the 4.34:1 failure.
            Assert.That(ReadThemeCss(fileName), Does.Not.Match(@"--st-warn-soft\s*:"),
                $"{fileName} still declares --st-warn-soft; it was replaced by --st-warn-tint");
        }
    }

    [Test]
    public void InteractiveControlsUseTheControlBorderToken()
    {
        foreach (var fileName in ThemeFiles)
        {
            var css = ReadThemeCss(fileName);

            foreach (var selector in ControlRules)
            {
                var body = ReadRuleBodies(css, selector, fileName);

                Assert.Multiple(() =>
                {
                    Assert.That(body, Does.Contain("var(--st-control-line)"),
                        $"{selector} in {fileName} is an interactive control, so its border must "
                        + "use --st-control-line to clear the 3:1 WCAG 1.4.11 floor");
                    Assert.That(body, Does.Not.Contain("var(--st-line)"),
                        $"{selector} in {fileName} must not fall back to --st-line, which is "
                        + "1.30:1 on light and 1.37:1 on dark");
                });
            }
        }
    }

    [Test]
    public void BothThemesDeclareADangerColourAndItsForeground()
    {
        foreach (var fileName in ThemeFiles)
        {
            var css = ReadThemeCss(fileName);

            Assert.Multiple(() =>
            {
                // #dc3545 was the last colour with no theme variant. Filled with a white
                // label it measured 4.53:1 and passed, while every text and border use of
                // the same value failed - 2.86:1 on the dark surface.
                Assert.That(css, Does.Match(@"--st-danger\s*:"),
                    $"{fileName} must declare --st-danger");
                Assert.That(css, Does.Match(@"--st-on-danger\s*:"),
                    $"{fileName} must declare --st-on-danger, the only foreground allowed on it");
                Assert.That(css, Does.Match(@"--st-danger-tint\s*:\s*#"),
                    $"{fileName} must declare --st-danger-tint, and solid - an alpha cannot be "
                    + "measured once, which is why --st-warn-soft was replaced");
            });
        }
    }

    [Test]
    public void DestructiveButtonsAreThemed()
    {
        foreach (var fileName in ThemeFiles)
        {
            // AGENTS.md keeps destructive actions on e-danger rather than restyling them
            // as CTAs, so the class stays and only its value becomes ours. Syncfusion's own
            // .e-btn.e-danger is two classes, so a bare .e-btn override cannot reach it.
            var body = ReadRuleBodies(ReadThemeCss(fileName), ".e-btn.e-danger", fileName);

            Assert.Multiple(() =>
            {
                Assert.That(body, Does.Contain("var(--st-danger)"),
                    $".e-btn.e-danger in {fileName} must use --st-danger");
                Assert.That(body, Does.Contain("var(--st-on-danger)"),
                    $".e-btn.e-danger in {fileName} must use --st-on-danger for its label");
            });
        }
    }

    [Test]
    public void NoRawDangerLiteralOutsideTheThemeSheets()
    {
        // A coloured box-shadow in app.css is how the recording ring ended up with no dark
        // variant in the first place - colour belongs in the theme sheets.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MusicSalesApp.slnx")))
            dir = dir.Parent;

        var appCss = File.ReadAllText(Path.Combine(dir!.FullName, "MusicSalesApp", "wwwroot", "app.css"));

        Assert.That(appCss, Does.Not.Contain("#dc3545"),
            "app.css carries layout, not colour - a danger value there cannot have a dark variant");
    }

    [Test]
    public void SurfacesKeepTheOriginalLineToken()
    {
        foreach (var fileName in ThemeFiles)
        {
            var css = ReadThemeCss(fileName);

            foreach (var selector in SurfaceRules)
            {
                // A card edge is decoration. Promoting it to the heavier control border would
                // make every card on the site look boxed in.
                Assert.That(ReadRuleBodies(css, selector, fileName), Does.Contain("var(--st-line)"),
                    $"{selector} in {fileName} is a surface, not a control, and keeps --st-line");
            }
        }
    }
}
