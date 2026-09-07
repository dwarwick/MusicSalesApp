using System.Text.RegularExpressions;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The follow bell is styled by JOINING the like/dislike selector groups, never by rules of its own.
/// </summary>
/// <remarks>
/// It shipped once with its own bespoke block, which looked right in isolation and wrong on a card:
/// the card overrides strip the button chrome from like and dislike
/// (<c>.card-ai-actions-row</c> - "bare icon + count, the card already carries enough boxes") and the
/// bell was not in those groups, so it wore a grey square the others did not. Two theme sheets and
/// two breakpoint sheets each had overrides that had to be matched by hand.
///
/// Joining the groups makes that impossible to get wrong again, and is why light mode needed no
/// separate fix. These tests read the stylesheets as text, in the same spirit as
/// ControlBorderTokenCssTests.
/// </remarks>
[TestFixture]
public class FollowBellCssTests
{
    private static readonly string WwwRoot = ResolveWwwRoot();

    private static string ResolveWwwRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "MusicSalesApp", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate MusicSalesApp/wwwroot.");
    }

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(WwwRoot, fileName));

    /// <summary>
    /// Inside the players the bell takes player colours, never page-content colours.
    /// </summary>
    /// <remarks>
    /// The players are dark in BOTH site themes, so the generic like/dislike colours - which are
    /// page content, defined twice and flipped by the toggle - paint a white box with black glyphs
    /// onto a near-black hero in light mode. That is what the bell was reported for. The scoped
    /// rules live in app.css precisely because there is nothing theme-dependent about them; putting
    /// a light variant in light.css is the mistake this guards against.
    /// </remarks>
    [Test]
    public void ThePlayersStyleTheBellFromThePlayerPalette()
    {
        var app = Read("app.css");

        Assert.Multiple(() =>
        {
            Assert.That(
                app,
                Does.Contain(".playlist-player-container .follow-artist-bell"),
                "the playlist player must override the page-content colours");

            Assert.That(
                app,
                Does.Contain(".song-player-container .follow-artist-bell"),
                "and so must the song player");

            Assert.That(
                app,
                Does.Contain(".playlist-player-container .playlist-meta-row .follow-artist-bell"),
                "the hero meta row is text, so the bell is bare there");
        });

        foreach (var themeSheet in new[] { "light.css", "dark.css" })
        {
            Assert.That(
                Read(themeSheet),
                Does.Not.Contain("-player-container .follow-artist-bell"),
                $"{themeSheet} must not carry a theme variant of a player rule - the players do not "
                + "change with the site theme, and a second copy is what drifts");
        }
    }

    /// <summary>
    /// Every selector group that styles a like button must style the bell too.
    /// </summary>
    [TestCase("app.css")]
    [TestCase("light.css")]
    [TestCase("dark.css")]
    [TestCase("sm_app.css")]
    [TestCase("xs_app.css")]
    public void EveryLikeButtonGroupAlsoCoversTheBell(string fileName)
    {
        var css = Read(fileName);

        // A group is everything from the previous } (or the start) up to the {.
        var groups = Regex.Matches(css, @"(?:^|\})([^{}]*)\{", RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value)
            .Where(selector => selector.Contains(".like-button"))
            .ToList();

        Assert.That(groups, Is.Not.Empty, $"{fileName} should style like buttons.");

        foreach (var group in groups)
        {
            Assert.That(
                group,
                Does.Contain(".follow-artist-bell"),
                $"{fileName} styles like buttons without the bell: {Compact(group)}");
        }
    }

    /// <summary>
    /// And the bell must not acquire rules of its own, which is how it drifted out of step before.
    /// </summary>
    /// <remarks>
    /// One context is exempt, and only one: the artist hero's meta row renders no like button, so
    /// there is no group for the bell to join there. It is a line of plain text - track count,
    /// streams, followers - and the bell is bare inside it. Everywhere the two appear together the
    /// rule stands.
    /// </remarks>
    [TestCase("app.css")]
    [TestCase("light.css")]
    [TestCase("dark.css")]
    [TestCase("sm_app.css")]
    [TestCase("xs_app.css")]
    public void TheBellHasNoRulesOfItsOwn(string fileName)
    {
        var css = Read(fileName);

        var soloGroups = Regex.Matches(css, @"(?:^|\})([^{}]*)\{", RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value)
            .Where(selector => selector.Contains(".follow-artist-bell")
                               && !selector.Contains(".like-button")
                               && !selector.Contains(".playlist-meta-row"))
            .ToList();

        Assert.That(
            soloGroups,
            Is.Empty,
            $"{fileName} styles the bell on its own: {string.Join(" | ", soloGroups.Select(Compact))}");
    }

    /// <summary>
    /// The card strips button chrome from these icons, and the bell has to be stripped with them -
    /// this is the exact rule whose absence put a grey square around it.
    /// </summary>
    [TestCase("light.css")]
    [TestCase("dark.css")]
    public void TheCardStripsChromeFromTheBellToo(string fileName)
    {
        var css = Read(fileName);

        Assert.That(
            css,
            Does.Contain(".card-ai-actions-row .follow-artist-bell"),
            $"{fileName} must strip the card's button chrome from the bell.");
    }

    private static string Compact(string selector) =>
        Regex.Replace(selector, @"\s+", " ").Trim();
}
