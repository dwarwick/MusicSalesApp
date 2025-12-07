#nullable enable

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing user theme preferences.
/// </summary>
public class ThemeService : IThemeService
{
    private readonly IConfiguration _configuration;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly UserManager<ApplicationUser> _userManager;
    private string _currentTheme = "Light";

    public event Action? OnThemeChanged;

    public ThemeService(
        IConfiguration configuration,
        AuthenticationStateProvider authStateProvider,
        IDbContextFactory<AppDbContext> dbContextFactory,
        UserManager<ApplicationUser> userManager)
    {
        _configuration = configuration;
        _authStateProvider = authStateProvider;
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
    }

    public string CurrentTheme => _currentTheme;

    public bool IsDarkTheme => _currentTheme.Equals("Dark", StringComparison.OrdinalIgnoreCase);

    public string SyncfusionCssUrl
    {
        get
        {
            var baseUrl = _configuration["SyncfusionTheme"] ?? "https://cdn.syncfusion.com/blazor/31.2.2/styles/bootstrap5.3";
            return IsDarkTheme ? $"{baseUrl}-dark.css" : $"{baseUrl}.css";
        }
    }

    public string CustomCssFile => IsDarkTheme ? "dark.css" : "light.css";

    public async Task SetThemeAsync(string theme, bool persist = true)
    {
        var normalizedTheme = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        
        if (_currentTheme == normalizedTheme)
            return;

        _currentTheme = normalizedTheme;

        if (persist)
        {
            await PersistThemeAsync(normalizedTheme);
        }

        OnThemeChanged?.Invoke();
    }

    public async Task InitializeThemeAsync()
    {
        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity?.IsAuthenticated == true)
        {
            var appUser = await _userManager.GetUserAsync(user);
            if (appUser != null && !string.IsNullOrEmpty(appUser.Theme))
            {
                _currentTheme = appUser.Theme;
            }
        }
        else
        {
            _currentTheme = "Light";
        }

        OnThemeChanged?.Invoke();
    }

    private async Task PersistThemeAsync(string theme)
    {
        try
        {
            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            if (user.Identity?.IsAuthenticated == true)
            {
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
                var userId = _userManager.GetUserId(user);
                
                if (!string.IsNullOrEmpty(userId) && int.TryParse(userId, out var userIdInt))
                {
                    var appUser = await dbContext.Users.FindAsync(userIdInt);
                    if (appUser != null)
                    {
                        appUser.Theme = theme;
                        await dbContext.SaveChangesAsync();
                    }
                }
            }
        }
        catch (Exception)
        {
            // Log the error but don't throw - theme persistence is not critical
        }
    }
}
