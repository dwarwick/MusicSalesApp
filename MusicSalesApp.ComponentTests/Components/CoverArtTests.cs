using Bunit;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class CoverArtTests : BUnitTestBase
{
    private const string BaseUrl = "api/music/guid/guid-coverart.jpg?v=3";

    private static CoverArtSource WithVariants() => new(
        BaseUrl,
        new[]
        {
            new CoverArtVariantUrl(128, "api/music/guid/guid-coverart.jpg.w128.webp?v=3"),
            new CoverArtVariantUrl(320, "api/music/guid/guid-coverart.jpg.w320.webp?v=3"),
            new CoverArtVariantUrl(640, "api/music/guid/guid-coverart.jpg.w640.webp?v=3")
        });

    private static CoverArtSource WithoutVariants() => new(BaseUrl, Array.Empty<CoverArtVariantUrl>());

    private IRenderedComponent<CoverArt> Render(CoverArtSource source, Action<ComponentParameterCollectionBuilder<CoverArt>> extra = null)
        => TestContext.Render<CoverArt>(parameters =>
        {
            parameters.Add(p => p.Source, source);
            extra?.Invoke(parameters);
        });

    [Test]
    public void WithRenditions_EmitsACandidateListWithWidthDescriptors()
    {
        var img = Render(WithVariants()).Find("img");

        Assert.That(img.GetAttribute("srcset"), Is.EqualTo(
            "api/music/guid/guid-coverart.jpg.w128.webp?v=3 128w, " +
            "api/music/guid/guid-coverart.jpg.w320.webp?v=3 320w, " +
            "api/music/guid/guid-coverart.jpg.w640.webp?v=3 640w"));
    }

    [Test]
    public void WithRenditions_SrcStaysTheFullSizeMaster()
    {
        // The guaranteed-present fallback for anything that ignores srcset. Browsers that honour
        // srcset never fetch it.
        Assert.That(Render(WithVariants()).Find("img").GetAttribute("src"), Is.EqualTo(BaseUrl));
    }

    [Test]
    public void WithoutRenditions_EmitsAPlainImgWithNoSrcsetOrSizes()
    {
        // This is the whole safety story: before the backfill has run, or when generation failed
        // for one image, the markup is exactly what the site rendered before this feature existed.
        var img = Render(WithoutVariants()).Find("img");

        Assert.Multiple(() =>
        {
            Assert.That(img.GetAttribute("src"), Is.EqualTo(BaseUrl));
            Assert.That(img.HasAttribute("srcset"), Is.False);
            Assert.That(img.HasAttribute("sizes"), Is.False);
        });
    }

    [Test]
    public void WithoutAnImage_RendersNothingAtAll()
    {
        var component = Render(CoverArtSource.None);

        Assert.That(component.FindAll("img"), Is.Empty);
    }

    [Test]
    public void SizesAccompaniesTheCandidateList()
    {
        var img = Render(WithVariants(), p => p.Add(c => c.Sizes, CoverArtSizes.PlayerHero)).Find("img");

        Assert.That(img.GetAttribute("sizes"), Is.EqualTo(CoverArtSizes.PlayerHero));
    }

    [Test]
    public void DefaultsToLazyLoading()
        => Assert.That(Render(WithVariants()).Find("img").GetAttribute("loading"), Is.EqualTo("lazy"));

    [Test]
    public void Eager_OptsOutOfLazyLoadingForAboveTheFoldArtwork()
    {
        var img = Render(WithVariants(), p => p.Add(c => c.Eager, true)).Find("img");

        Assert.That(img.GetAttribute("loading"), Is.EqualTo("eager"));
    }

    [Test]
    public void AlwaysDecodesAsynchronously()
        => Assert.That(Render(WithVariants()).Find("img").GetAttribute("decoding"), Is.EqualTo("async"));

    [Test]
    public void PassesThroughTheCssClassAndAltText()
    {
        var img = Render(WithVariants(), p =>
        {
            p.Add(c => c.CssClass, "playlist-art");
            p.Add(c => c.Alt, "Night Drive cover");
        }).Find("img");

        Assert.Multiple(() =>
        {
            Assert.That(img.GetAttribute("class"), Is.EqualTo("playlist-art"));
            Assert.That(img.GetAttribute("alt"), Is.EqualTo("Night Drive cover"));
        });
    }

    [Test]
    public void IntrinsicDimensions_AreEmittedToReserveTheBox()
    {
        var img = Render(WithVariants(), p =>
        {
            p.Add(c => c.IntrinsicWidth, 140);
            p.Add(c => c.IntrinsicHeight, 140);
        }).Find("img");

        Assert.Multiple(() =>
        {
            Assert.That(img.GetAttribute("width"), Is.EqualTo("140"));
            Assert.That(img.GetAttribute("height"), Is.EqualTo("140"));
        });
    }

    [Test]
    public void UnmatchedAttributesArePassedThrough()
    {
        var img = Render(WithVariants(), p => p.AddUnmatched("data-song-id", "42")).Find("img");

        Assert.That(img.GetAttribute("data-song-id"), Is.EqualTo("42"));
    }

    [Test]
    public void ASingleRenditionStillProducesAValidCandidateList()
    {
        // The never-upscale fallback can leave one rendition at a non-ladder width.
        var source = new CoverArtSource(BaseUrl, new[] { new CoverArtVariantUrl(96, "small.webp") });

        Assert.That(Render(source).Find("img").GetAttribute("srcset"), Is.EqualTo("small.webp 96w"));
    }
}
