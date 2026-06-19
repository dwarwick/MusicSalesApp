using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Creator;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class SubmitTaxFormTests : BUnitTestBase
{
    [Test]
    public void SubmitTaxForm_OnTaxFormComplete_NavigatesToCreatorSettings()
    {
        SetupAuthorizedUser(1, "testuser@test.com");
        SetupRendererInfo();

        var cut = TestContext.Render<SubmitTaxForm>();
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();

        cut.Instance.OnTaxFormComplete("completed");

        Assert.That(navigationManager.Uri, Does.EndWith("/CreatorSettings"));
    }
}
