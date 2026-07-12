using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Security.Claims;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ThemeServiceTests
{
    private const string SyncfusionThemeBaseUrl = "https://cdn.syncfusion.com/blazor/33.1.44/styles/bootstrap5.3";

    [Test]
    public void SyncfusionCssUrl_ReturnsVersionedLightThemeUrl()
    {
        var service = CreateThemeService();

        Assert.That(service.SyncfusionCssUrl, Is.EqualTo($"{SyncfusionThemeBaseUrl}.css"));
    }

    [Test]
    public async Task SyncfusionCssUrl_ReturnsVersionedDarkThemeUrl()
    {
        var service = CreateThemeService();

        await service.SetThemeAsync("Dark", persist: false);

        Assert.That(service.SyncfusionCssUrl, Is.EqualTo($"{SyncfusionThemeBaseUrl}-dark.css"));
    }

    private static ThemeService CreateThemeService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                [AppSettingKeys.SyncfusionTheme] = SyncfusionThemeBaseUrl
            })
            .Build();

        var authStateProvider = new Mock<AuthenticationStateProvider>();
        authStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

        var dbContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        var userStore = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        return new ThemeService(
            configuration,
            authStateProvider.Object,
            dbContextFactory.Object,
            userManager.Object);
    }
}
