using System.Reflection;
using MusicSalesApp.Components;

namespace MusicSalesApp.Tests.Components;

[TestFixture]
public class GoogleConsentModeTests
{
    [Test]
    public void AppRendersConsentDefaultBeforeGoogleTagManager()
    {
        var markup = ReadProjectFile("MusicSalesApp", "Components", "App.razor");

        Assert.That(markup.IndexOf("googleConsentDefaultHtml", StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        Assert.That(
            markup.IndexOf("googleConsentDefaultHtml", StringComparison.Ordinal),
            Is.LessThan(markup.IndexOf("googleTagManagerHeadHtml", StringComparison.Ordinal)));
    }

    [Test]
    public void ConsentDefaultIncludesConsentModeV2DeniedDefaults()
    {
        var html = InvokeBuildGoogleConsentDefaultHtml();

        Assert.That(html, Does.Contain("gtag('consent', 'default'"));
        Assert.That(html, Does.Contain("'ad_storage': 'denied'"));
        Assert.That(html, Does.Contain("'analytics_storage': 'denied'"));
        Assert.That(html, Does.Contain("'ad_user_data': 'denied'"));
        Assert.That(html, Does.Contain("'ad_personalization': 'denied'"));
        Assert.That(html, Does.Contain("'wait_for_update': 500"));
    }

    private static string InvokeBuildGoogleConsentDefaultHtml()
    {
        var method = typeof(App).GetMethod(
            "BuildGoogleConsentDefaultHtml",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null);
        return (string)method!.Invoke(null, null)!;
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

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
