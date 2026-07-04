#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Pages;

[Authorize]
public class RefreshSignInPageModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<RefreshSignInPageModel> _logger;

    public RefreshSignInPageModel(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        ILogger<RefreshSignInPageModel> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<IActionResult> OnGetAsync([FromQuery] string? returnUrl = null)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return LocalRedirect(AppPageRoutes.Login);
        }

        await _signInManager.RefreshSignInAsync(user);
        _logger.LogInformation("Refreshed sign-in for user {UserId}", user.Id);

        return LocalRedirect(GetSafeReturnUrl(returnUrl));
    }

    private string GetSafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl))
        {
            return AppPageRoutes.CreatorSettings;
        }

        var path = returnUrl.Split('?', 2)[0];
        if (string.Equals(path, AppPageRoutes.RefreshSignIn, StringComparison.OrdinalIgnoreCase))
        {
            return AppPageRoutes.CreatorSettings;
        }

        return returnUrl;
    }
}
