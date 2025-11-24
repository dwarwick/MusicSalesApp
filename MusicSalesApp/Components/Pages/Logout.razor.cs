using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class LogoutModel : BlazorBase
{
    protected override async Task OnInitializedAsync()
    {
        await AuthenticationService.LogoutAsync();
        NavigationManager.NavigateTo("/login", forceLoad: true);
    }
}
