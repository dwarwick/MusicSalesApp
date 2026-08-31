using Bunit;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class UploadFilesTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        // Fixture-wide because the page now hosts an SfDialog - the leave prompt - and
        // Syncfusion reads RendererInfo on every render, not only when a dialog is shown.
        SetupRendererInfo();
    }

    [Test]
    public void UploadFiles_DoesNotMountTheAnimation_WhenNotUploading()
    {
        // The spinner used to be mounted always and merely display:none'd, so every view
        // of this form built a Lottie player and pulled its WASM renderer for an element
        // nobody could see. It is now gated on _isUploading, so an idle form mounts nothing.
        var cut = TestContext.Render<UploadFiles>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("dotlottie-wc"));
            // Query the DOM, not the raw markup: this component ships an inline <style>
            // block that names .upload-spinner, so a text search always matches.
            Assert.That(cut.FindAll(".upload-spinner"), Is.Empty);
        });
    }

    [Test]
    public void UploadFiles_DisplaysSupportedFormats()
    {
        // Act
        var cut = TestContext.Render<UploadFiles>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("MP3, WAV, FLAC, OGG, M4A, AAC, WMA"));
    }

    [Test]
    public void UploadFiles_DisplaysAlbumArtFormat()
    {
        // Act
        var cut = TestContext.Render<UploadFiles>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("JPEG"));
        Assert.That(cut.Markup, Does.Contain("PNG"));
    }

    [Test]
    public void UploadFiles_IndicatesCoverArtIsOptional()
    {
        // Act
        var cut = TestContext.Render<UploadFiles>();

        // Assert - Cover art is now optional
        Assert.That(cut.Markup, Does.Contain("optional"));
    }

    [Test]
    public void UploadFiles_InitiallyNoProgressTable()
    {
        // Act
        var cut = TestContext.Render<UploadFiles>();

        // Assert - none of the three progress sections should render before files are selected.
        // Named individually rather than checking one string, so renaming a heading cannot quietly
        // turn this into an assertion about text that no longer exists anywhere.
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("Overall Progress"));
            Assert.That(cut.Markup, Does.Not.Contain("Receiving Files"));
            Assert.That(cut.Markup, Does.Not.Contain("Processing Progress"));
        });
    }

    [Test]
    public void UploadFiles_InitiallyNoValidationError()
    {
        // Act
        var cut = TestContext.Render<UploadFiles>();

        // Assert
        Assert.That(cut.Markup, Does.Not.Contain("Validation Error"));
    }

    // -----------------------------------------------------------------
    // Leaving with work still on the page
    // -----------------------------------------------------------------
    //
    // The <NavigationLock> at the top of this page only ever caught a NavigateTo made from inside
    // the circuit. Routing here is static SSR with per-page interactive islands, so an ordinary
    // link is enhanced navigation and never reaches the circuit's NavigationManager - which is why
    // refreshing warned (ConfirmExternalNavigation, a beforeunload path that still works) while a
    // nav-menu click discarded a reviewed batch without a word. Anchors are now held in JavaScript
    // and arrive at RequestLeave, which is what these cover.

    /// <summary>The rendered page, past the first render that installs the guard.</summary>
    private IRenderedComponent<UploadFiles> RenderPage() => TestContext.Render<UploadFiles>();

    /// <summary>Puts the page in the state the guard exists for: reviewed, nothing sent yet.</summary>
    private static Task GivenABatchAwaitingUpload(IRenderedComponent<UploadFiles> cut) =>
        cut.InvokeAsync(() => cut.Instance._awaitingTitleConfirmation = true);

    private static void Answer(IRenderedComponent<UploadFiles> cut, string label) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == label).Click();

    [Test]
    public async Task AnIdleFormLetsALinkThroughWithoutAsking()
    {
        // Every link on the page asks .NET first, so an idle form has to answer immediately.
        // Interrupting somebody who has selected nothing would be the fastest way to teach them to
        // dismiss this prompt without reading it.
        var cut = RenderPage();

        Assert.That(await cut.InvokeAsync(() => cut.Instance.RequestLeave()), Is.True);
    }

    [Test]
    public async Task AReviewedBatchIsNotDiscardedWithoutBeingAskedAbout()
    {
        // The reported failure: files chosen, titles reviewed, nothing uploaded yet - and a click on
        // the nav menu threw the batch away silently.
        var cut = RenderPage();
        await GivenABatchAwaitingUpload(cut);

        var decision = cut.InvokeAsync(() => cut.Instance.RequestLeave());
        cut.WaitForState(() => cut.Markup.Contains("Leave this page?"), TimeSpan.FromSeconds(5));

        Answer(cut, "No");

        Assert.That(await decision, Is.False, "They said no, so they stay.");
    }

    [Test]
    public async Task TheWarningNamesWhatLeavingWouldCost()
    {
        // The reason this is a dialog rather than the browser's confirm box: a native one names the
        // site and can say nothing about which files are at stake.
        var cut = RenderPage();
        await GivenABatchAwaitingUpload(cut);

        _ = cut.InvokeAsync(() => cut.Instance.RequestLeave());
        cut.WaitForState(() => cut.Markup.Contains("Leave this page?"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("have not been uploaded yet"));

        Answer(cut, "No");
    }

    [Test]
    public async Task ConfirmingTheWarningLetsThemLeave()
    {
        var cut = RenderPage();
        await GivenABatchAwaitingUpload(cut);

        var decision = cut.InvokeAsync(() => cut.Instance.RequestLeave());
        cut.WaitForState(() => cut.Markup.Contains("Leave this page?"), TimeSpan.FromSeconds(5));

        Answer(cut, "Yes");

        Assert.That(await decision, Is.True);
    }

    [Test]
    public void TheLinkGuardIsArmedWhenThePageLoads()
    {
        // Without this the page still warns on refresh and stays silent on every link, which is
        // exactly the half-working state this fixes.
        RenderPage();

        Assert.That(
            TestContext.JSInterop.Invocations.Any(i => i.Identifier == "arm"),
            Is.True,
            "The anchor guard has to be installed, or nothing catches a link.");
    }
}
