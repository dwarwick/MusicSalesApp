using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Pages;

namespace MusicSalesApp.Tests.Components;

[TestFixture]
public class LogoutPageTests
{
    private Mock<SignInManager<ApplicationUser>> _mockSignInManager;
    private Mock<ILogger<LogoutPageModel>> _mockLogger;
    private LogoutPageModel _pageModel;

    [SetUp]
    public void SetUp()
    {
        var mockUserStore = new Mock<IUserStore<ApplicationUser>>();
        var mockUserManager = new Mock<UserManager<ApplicationUser>>(
            mockUserStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _mockSignInManager = new Mock<SignInManager<ApplicationUser>>(
            mockUserManager.Object,
            Mock.Of<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<ApplicationUser>>(),
            null!, null!, null!, null!);

        _mockLogger = new Mock<ILogger<LogoutPageModel>>();
        _pageModel = new LogoutPageModel(_mockSignInManager.Object, _mockLogger.Object);
    }

    [Test]
    public async Task OnGetAsync_SignsOutAndRedirectsToHomePage()
    {
        // Act
        var result = await _pageModel.OnGetAsync();

        // Assert
        _mockSignInManager.Verify(x => x.SignOutAsync(), Times.Once);
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo("/"));
    }

    [Test]
    public async Task OnPostAsync_SignsOutAndRedirectsToHomePage()
    {
        // Act
        var result = await _pageModel.OnPostAsync();

        // Assert
        _mockSignInManager.Verify(x => x.SignOutAsync(), Times.Once);
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo("/"));
    }
}
