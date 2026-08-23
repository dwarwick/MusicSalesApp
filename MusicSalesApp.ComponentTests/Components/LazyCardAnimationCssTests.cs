#nullable enable
using System.Text.RegularExpressions;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// Guards the one CSS invariant the lazy card animation depends on. This is asserted against the
/// stylesheet rather than against rendered markup because the failure is invisible in markup: the
/// host element renders exactly the same either way, and the only symptom is that the animation
/// silently never appears. It took a deploy to spot the first time.
/// </summary>
[TestFixture]
public class LazyCardAnimationCssTests
{
    private static string ReadAppCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MusicSalesApp.slnx")))
            dir = dir.Parent;

        Assert.That(dir, Is.Not.Null, "could not locate the repository root from the test assembly");

        var cssPath = Path.Combine(dir!.FullName, "MusicSalesApp", "wwwroot", "app.css");
        Assert.That(File.Exists(cssPath), Is.True, $"app.css not found at {cssPath}");

        return File.ReadAllText(cssPath);
    }

    private static string ReadRuleBody(string css, string selector)
    {
        // Matches the selector only when it stands alone at the start of a rule, so a descendant
        // rule like ".card-album-art-animation.is-animating .card-lottie-host" is not picked up.
        var match = Regex.Match(css, $@"(?<![\w.-]){Regex.Escape(selector)}\s*\{{([^}}]*)\}}");
        Assert.That(match.Success, Is.True, $"no rule found for {selector} in app.css");
        return match.Groups[1].Value;
    }

    [Test]
    public void TheLottieHostIsNeverRemovedFromLayoutWhileEmpty()
    {
        // IntersectionObserver is watching this element. An element with no layout box is never
        // reported as intersecting, so hiding it while empty deadlocks the feature outright: it
        // cannot become visible until it is filled, and it cannot be filled until it is observed
        // as visible. It must stay laid out and simply paint nothing while it has no children.
        var body = ReadRuleBody(ReadAppCss(), ".card-lottie-host");

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Not.Match(@"display\s*:\s*none"),
                "display:none removes the layout box, so IntersectionObserver can never fire for it");
            Assert.That(body, Does.Not.Match(@"visibility\s*:\s*hidden"),
                "visibility:hidden would hide a mounted player, which is what .is-animating is for");
            Assert.That(body, Does.Not.Match(@"content-visibility\s*:\s*hidden"),
                "content-visibility:hidden skips rendering the subtree the player is mounted into");
        });
    }

    [Test]
    public void TheLottieHostHasAContainingBlockToFill()
    {
        // The host is inset:0, so its wrapper has to establish a containing block or it collapses
        // and the animation is mounted into a zero-size box - visible to no observer and no user.
        var css = ReadAppCss();

        Assert.That(ReadRuleBody(css, ".card-lottie-host"), Does.Match(@"position\s*:\s*absolute"));

        var wrappers = Regex.Matches(css, @"(?<![\w.-])\.card-album-art-animation\s*\{([^}]*)\}");
        Assert.That(wrappers, Is.Not.Empty, "no .card-album-art-animation rule found");
        Assert.That(
            wrappers.Any(w => Regex.IsMatch(w.Groups[1].Value, @"position\s*:\s*relative")),
            Is.True,
            "no declaration of .card-album-art-animation sets position:relative");
    }
}
