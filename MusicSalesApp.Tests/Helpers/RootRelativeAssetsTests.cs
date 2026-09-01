using Microsoft.AspNetCore.Components;
using MusicSalesApp.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class RootRelativeAssetsTests
{
    /// <summary>
    /// Mirrors what MapStaticAssets hands the component: a fingerprinted URL carrying a "label"
    /// property naming the asset it was built from, which is the key call sites look up.
    /// </summary>
    private static ResourceAssetCollection CollectionWith(string fingerprintedUrl, string label) =>
        new([new ResourceAsset(fingerprintedUrl, [new ResourceAssetProperty("label", label)])]);

    [Test]
    public void Root_PrefixesASlash_SoTheUrlIsResolvedAgainstTheOriginNotTheDocument()
    {
        var assets = CollectionWith("js/dashboard-helper.x7hsw76v7g.js", "js/dashboard-helper.js");

        Assert.That(assets.Root("js/dashboard-helper.js"), Is.EqualTo("/js/dashboard-helper.x7hsw76v7g.js"));
    }

    [Test]
    public void Root_KeepsTheFingerprint()
    {
        var assets = CollectionWith("app.k2f8s1.css", "app.css");

        Assert.That(assets.Root("app.css"), Does.Contain("k2f8s1"));
    }

    [Test]
    public void Indexer_IsStillDocumentRelative_WhichIsTheBehaviourThisWrapperExistsToReplace()
    {
        var assets = CollectionWith("app.k2f8s1.css", "app.css");

        // Guard rather than a behaviour we want: if a framework update ever makes the indexer
        // root-relative on its own, Root() becomes redundant and this test says so.
        Assert.That(assets["app.css"], Does.Not.StartWith("/"));
    }

    [Test]
    public void Root_DoesNotDoubleSlash_WhenTheResolvedUrlIsAlreadyRooted()
    {
        var assets = CollectionWith("/app.k2f8s1.css", "app.css");

        Assert.That(assets.Root("app.css"), Is.EqualTo("/app.k2f8s1.css"));
    }

    [TestCase("https://cdn.example.net/app.css")]
    [TestCase("http://cdn.example.net/app.css")]
    public void Root_LeavesAnAbsoluteUrlAlone(string absoluteUrl)
    {
        var assets = CollectionWith(absoluteUrl, "app.css");

        Assert.That(assets.Root("app.css"), Is.EqualTo(absoluteUrl));
    }

    [Test]
    public void Root_RootsAnUnmappedKey_SoAMissingManifestEntryStillCannotEscapeTheOrigin()
    {
        var assets = new ResourceAssetCollection([]);

        // The indexer returns an unmapped key verbatim rather than throwing - which is also what
        // happens under bUnit, where there is no manifest at all.
        Assert.That(assets.Root("js/not-in-the-manifest.js"), Is.EqualTo("/js/not-in-the-manifest.js"));
    }

    [Test]
    public void Root_Throws_WhenTheKeyIsMissing()
    {
        var assets = new ResourceAssetCollection([]);

        Assert.Throws<ArgumentException>(() => assets.Root(" "));
    }
}
