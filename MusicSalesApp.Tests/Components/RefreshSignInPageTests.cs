using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Pages;
using System.Security.Claims;

namespace MusicSalesApp.Tests.Components;

[TestFixture]
public class RefreshSignInPageTests
{
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<SignInManager<ApplicationUser>> _mockSignInManager;
    private Mock<ILogger<RefreshSignInPageModel>> _mockLogger;
    private Mock<IUrlHelper> _mockUrlHelper;
    private RefreshSignInPageModel _pageModel;
    private ApplicationUser _user;

    [SetUp]
    public void SetUp()
    {
        var mockUserStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            mockUserStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _mockSignInManager = new Mock<SignInManager<ApplicationUser>>(
            _mockUserManager.Object,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<ApplicationUser>>(),
            null!, null!, null!, null!);

        _mockLogger = new Mock<ILogger<RefreshSignInPageModel>>();
        _mockUrlHelper = new Mock<IUrlHelper>();
        _user = new ApplicationUser
        {
            Id = 42,
            UserName = "creator@test.com",
            Email = "creator@test.com"
        };

        _mockUserManager
            .Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(_user);

        _pageModel = new RefreshSignInPageModel(
            _mockSignInManager.Object,
            _mockUserManager.Object,
            _mockLogger.Object)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, _user.Id.ToString())
                    }, "TestAuth"))
                }
            },
            Url = _mockUrlHelper.Object
        };
    }

    [Test]
    public async Task OnGetAsync_RefreshesSignInAndRedirectsToLocalReturnUrl()
    {
        var returnUrl = AppPageRoutes.CreatorSettings;
        _mockUrlHelper.Setup(x => x.IsLocalUrl(returnUrl)).Returns(true);

        var result = await _pageModel.OnGetAsync(returnUrl);

        _mockSignInManager.Verify(x => x.RefreshSignInAsync(_user), Times.Once);
        var redirect = result as LocalRedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Is.EqualTo(returnUrl));
    }

    [Test]
    public async Task OnGetAsync_RejectsExternalReturnUrl()
    {
        const string returnUrl = "https://evil.example/CreatorSettings";
        _mockUrlHelper.Setup(x => x.IsLocalUrl(returnUrl)).Returns(false);

        var result = await _pageModel.OnGetAsync(returnUrl);

        _mockSignInManager.Verify(x => x.RefreshSignInAsync(_user), Times.Once);
        var redirect = result as LocalRedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Is.EqualTo(AppPageRoutes.CreatorSettings));
    }

    [Test]
    public async Task OnGetAsync_RejectsSelfReturnUrl()
    {
        var returnUrl = $"{AppPageRoutes.RefreshSignIn}?{ExternalAuthFormFields.ReturnUrl}=%2FCreatorSettings";
        _mockUrlHelper.Setup(x => x.IsLocalUrl(returnUrl)).Returns(true);

        var result = await _pageModel.OnGetAsync(returnUrl);

        _mockSignInManager.Verify(x => x.RefreshSignInAsync(_user), Times.Once);
        var redirect = result as LocalRedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Is.EqualTo(AppPageRoutes.CreatorSettings));
    }

    [Test]
    public async Task OnGetAsync_RedirectsToLogin_WhenUserCannotBeLoaded()
    {
        _mockUserManager
            .Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null!);

        var result = await _pageModel.OnGetAsync(AppPageRoutes.CreatorSettings);

        _mockSignInManager.Verify(x => x.RefreshSignInAsync(It.IsAny<ApplicationUser>()), Times.Never);
        var redirect = result as LocalRedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Is.EqualTo(AppPageRoutes.Login));
    }
}
