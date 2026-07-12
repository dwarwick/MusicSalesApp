namespace MusicSalesApp.Tests.Components;

[TestFixture]
public class PublicFeaturedMusicTests
{
    [Test]
    public void Home_MovesExistingFeaturedMusicRenderingBeforeHero()
    {
        var markup = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Public", "Home.razor");
        var subscriptionIslandMarkup = ReadProjectFile(
            "MusicSalesApp",
            "Components",
            "Pages",
            "Public",
            "HomeSubscriptionOffer.razor");
        var featuredIndex = markup.IndexOf("<section class=\"featured-music-section\">", StringComparison.Ordinal);
        var subscriptionIslandIndex = markup.IndexOf("<HomeSubscriptionOffer", StringComparison.Ordinal);

        Assert.That(featuredIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(subscriptionIslandIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(featuredIndex, Is.LessThan(subscriptionIslandIndex));
        Assert.That(subscriptionIslandMarkup, Does.Contain("<section class=\"hero-section\">"));
        Assert.That(markup, Does.Contain("<MusicLibrary ShowHomePageFeatured=\"true\""));
        Assert.That(markup, Does.Not.Contain("FeaturedMusicStrip"));
    }

    [Test]
    public void NewCreatorSignUp_MovesExistingFeaturedMusicRenderingBeforeCreatorCopy()
    {
        var markup = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Public", "NewCreatorSignUp.razor");
        var featuredIndex = markup.IndexOf("<section class=\"featured-music-section\">", StringComparison.Ordinal);

        Assert.That(featuredIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(featuredIndex, Is.LessThan(markup.IndexOf("<h1", StringComparison.Ordinal)));
        Assert.That(markup, Does.Contain("<MusicLibrary ShowHomePageFeatured=\"true\""));
        Assert.That(markup, Does.Not.Contain("FeaturedMusicStrip"));
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
