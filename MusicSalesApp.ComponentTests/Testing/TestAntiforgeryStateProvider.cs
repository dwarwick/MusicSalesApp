#nullable enable
using Microsoft.AspNetCore.Components.Forms;

namespace MusicSalesApp.ComponentTests.Testing;

/// <summary>
/// Stands in for the framework's <c>DefaultAntiforgeryStateProvider</c>, which is internal and is
/// registered by <c>AddRazorComponents()</c> — neither of which bUnit gives us.
///
/// <para>
/// Without a provider the <c>&lt;AntiforgeryToken /&gt;</c> component renders nothing at all,
/// silently, so a page that has lost its antiforgery field looks identical to one that never had
/// one. Registering this keeps <c>Login_HasAntiforgeryToken</c> meaningful instead of having to
/// weaken the assertion to match whatever bUnit happens to produce.
/// </para>
/// </summary>
public sealed class TestAntiforgeryStateProvider : AntiforgeryStateProvider
{
    /// <summary>The value the token field renders with, so tests can assert on something concrete.</summary>
    public const string TokenValue = "test-antiforgery-token";

    private static readonly AntiforgeryRequestToken Token = new(TokenValue, "__RequestVerificationToken");

    public override AntiforgeryRequestToken? GetAntiforgeryToken() => Token;
}
