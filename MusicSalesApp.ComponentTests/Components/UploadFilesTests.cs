using Bunit;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class UploadFilesTests : BUnitTestBase
{
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
}
