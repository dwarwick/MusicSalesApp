using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MusicSalesApp.Models;

namespace MusicSalesApp.Pages;

public class AccountModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<AccountModel> _logger;

    public AccountModel(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        ILogger<AccountModel> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<IActionResult> OnPostAsync(string action, [FromForm] string username, [FromForm] string password, [FromForm] string returnUrl = "/")
    {
        if (action?.ToLower() == "login")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                return Redirect($"/login?error=Invalid credentials");
            }

            // Try to find user by email first, then by username
            var user = await _userManager.FindByEmailAsync(username)
                       ?? await _userManager.FindByNameAsync(username);

            if (user == null)
            {
                _logger.LogWarning("Login failed: User not found for username: {Username}", username);
                return Redirect($"/login?error=Invalid username or password");
            }

            // Validate password and sign in with lockout protection
            var result = await _signInManager.PasswordSignInAsync(user, password, isPersistent: true, lockoutOnFailure: true);

            if (result.Succeeded)
            {
                _logger.LogInformation("User {Username} logged in successfully", username);
                return Redirect(returnUrl);
            }

            if (result.IsLockedOut)
            {
                _logger.LogWarning("User account locked out: {Username}", username);
                return Redirect($"/login?error=Account locked out");
            }

            _logger.LogWarning("Login failed for user {Username}: Invalid password", username);
            return Redirect($"/login?error=Invalid username or password");
        }
        else if (action?.ToLower() == "logout")
        {
            await _signInManager.SignOutAsync();
            _logger.LogInformation("User logged out successfully");
            return Redirect("/login");
        }

        return Redirect("/");
    }
}
