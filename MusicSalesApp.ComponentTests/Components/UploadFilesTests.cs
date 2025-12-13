using Bunit;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class UploadFilesTests : BUnitTestBase
{
    [Test]
    public void UploadFiles_HasInstructions()
    {
        // Act
        var cut = TestContext.Render<UploadFiles>();

        // Assert - Check for new instructions about pairing files
        Assert.That(cut.Markup, Does.Contain("Upload MP3 files paired with their album art"));
    }

    [Test]
    public void UploadFiles_HasUploadZone()
    {
        // Act
        var cut = TestContext.Render<UploadFiles>();

        // Assert
        var uploadZone = cut.Find(".upload-zone");
        Assert.That(uploadZone, Is.Not.Null);
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
    public void UploadFiles_InitiallyNoProgressTable()
    {
        // Act
        var cut = TestContext.Render<UploadFiles>();

        // Assert
        Assert.That(cut.Markup, Does.Not.Contain("Upload Progress"));
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
