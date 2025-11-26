#pragma warning disable CS0618, CS0619
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class UploadFilesTests
{
    private BunitContext _testContext;
    private Mock<IAuthenticationService> _mockAuthService;

    [SetUp]
    public void Setup()
    {
        _testContext = new BunitContext();

        // Register mock services
        _mockAuthService = new Mock<IAuthenticationService>();

        _testContext.Services.AddSingleton(_mockAuthService.Object);
        _testContext.Services.AddAuthorizationCore();

        var mockAuthStateProvider = new Mock<AuthenticationStateProvider>();
        _testContext.Services.AddSingleton<AuthenticationStateProvider>(mockAuthStateProvider.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _testContext?.Dispose();
    }

    [Test]
    public void UploadFiles_HasDestinationFolderInput()
    {
        // Act
        var cut = _testContext.Render<UploadFiles>();

        // Assert
        var input = cut.Find("#destinationFolder");
        Assert.That(input, Is.Not.Null);
    }

    [Test]
    public void UploadFiles_HasUploadZone()
    {
        // Act
        var cut = _testContext.Render<UploadFiles>();

        // Assert
        var uploadZone = cut.Find(".upload-zone");
        Assert.That(uploadZone, Is.Not.Null);
    }

    [Test]
    public void UploadFiles_HasBrowseButton()
    {
        // Act
        var cut = _testContext.Render<UploadFiles>();

        // Assert
        var label = cut.Find("label[for='fileInput']");
        Assert.That(label.TextContent, Does.Contain("Browse Files"));
    }

    [Test]
    public void UploadFiles_DisplaysSupportedFormats()
    {
        // Act
        var cut = _testContext.Render<UploadFiles>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("MP3, WAV, FLAC, OGG, M4A, AAC, WMA"));
    }

    [Test]
    public void UploadFiles_InitiallyNoProgressTable()
    {
        // Act
        var cut = _testContext.Render<UploadFiles>();

        // Assert
        Assert.That(cut.Markup, Does.Not.Contain("Upload Progress"));
    }
}
#pragma warning restore CS0618, CS0619
