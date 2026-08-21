#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Moq;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// Guards the failure that made the home page's playlists section come and go: an
/// AuthenticationStateProvider that could only see the user while an HttpContext was in scope.
/// There is no HttpContext inside a running Blazor Server circuit, so every interactive component
/// was told nobody was signed in - intermittently, because whether a context happened to be in
/// scope depended on whether you refreshed the page or arrived by enhanced navigation.
/// </summary>
[TestFixture]
public class CircuitAuthenticationStateProviderTests
{
    private Mock<IHttpContextAccessor> _httpContextAccessor = null!;
    private CircuitAuthenticationStateProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _httpContextAccessor = new Mock<IHttpContextAccessor>();
        _provider = new CircuitAuthenticationStateProvider(_httpContextAccessor.Object, NullLogger<CircuitAuthenticationStateProvider>.Instance);
    }

    private static ClaimsPrincipal SignedIn(string name) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }, "TestAuthType"));

    private void WithHttpContext(ClaimsPrincipal user) =>
        _httpContextAccessor.Setup(x => x.HttpContext).Returns(new DefaultHttpContext { User = user });

    private void WithNoHttpContext() =>
        _httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);

    [Test]
    public async Task ReadsTheUserFromHttpContext_DuringStaticSsr()
    {
        WithHttpContext(SignedIn("ssr@user.test"));

        var state = await _provider.GetAuthenticationStateAsync();

        Assert.That(state.User.Identity!.IsAuthenticated, Is.True);
        Assert.That(state.User.Identity.Name, Is.EqualTo("ssr@user.test"));
    }

    [Test]
    public async Task KeepsTheUser_OnceTheCircuitHasSuppliedIt_EvenWithNoHttpContext()
    {
        // THE REGRESSION. This is exactly the circuit case: Blazor hands the identity over as the
        // circuit starts, and from then on there is no HttpContext for the rest of its life.
        // The old provider returned anonymous here, which is what broke every interactive
        // component that read auth in OnAfterRenderAsync.
        WithNoHttpContext();
        _provider.SetAuthenticationState(
            Task.FromResult(new AuthenticationState(SignedIn("circuit@user.test"))));

        var state = await _provider.GetAuthenticationStateAsync();

        Assert.That(state.User.Identity!.IsAuthenticated, Is.True,
            "a circuit-supplied identity must outlive the HTTP request that created the circuit");
        Assert.That(state.User.Identity.Name, Is.EqualTo("circuit@user.test"));
    }

    [Test]
    public async Task DoesNotCacheAnonymous_SoALateCircuitHandoverStillWins()
    {
        // Order is not guaranteed: a component can ask before the circuit hands the identity over.
        // Caching the anonymous answer at that point would resurrect the original bug.
        WithNoHttpContext();

        var before = await _provider.GetAuthenticationStateAsync();
        Assert.That(before.User.Identity!.IsAuthenticated, Is.False);

        _provider.SetAuthenticationState(
            Task.FromResult(new AuthenticationState(SignedIn("late@user.test"))));
        var after = await _provider.GetAuthenticationStateAsync();

        Assert.That(after.User.Identity!.IsAuthenticated, Is.True);
        Assert.That(after.User.Identity.Name, Is.EqualTo("late@user.test"));
    }

    [Test]
    public async Task ReportsAnonymous_WhenThereIsNeitherAContextNorACircuitIdentity()
    {
        WithNoHttpContext();

        var state = await _provider.GetAuthenticationStateAsync();

        Assert.That(state.User.Identity!.IsAuthenticated, Is.False);
    }

    [Test]
    public async Task ReportsAnonymous_ForAnUnauthenticatedHttpContext()
    {
        WithHttpContext(new ClaimsPrincipal(new ClaimsIdentity()));

        var state = await _provider.GetAuthenticationStateAsync();

        Assert.That(state.User.Identity!.IsAuthenticated, Is.False);
    }

    [Test]
    public async Task NotifyAuthenticationStateChanged_InsideACircuit_KeepsTheCircuitIdentity()
    {
        // AuthenticationService can fire a notify from interactive code, where there is no
        // HttpContext. Nulling the circuit-supplied identity there would republish anonymous and
        // visually sign the user out - the original bug in a new hat.
        WithNoHttpContext();
        _provider.SetAuthenticationState(
            Task.FromResult(new AuthenticationState(SignedIn("kept@user.test"))));

        _provider.NotifyAuthenticationStateChanged();

        var state = await _provider.GetAuthenticationStateAsync();
        Assert.That(state.User.Identity!.IsAuthenticated, Is.True,
            "an in-circuit notify must not wipe the circuit-supplied identity");
        Assert.That(state.User.Identity.Name, Is.EqualTo("kept@user.test"));
    }

    [Test]
    public async Task NotifyAuthenticationStateChanged_RereadsAfterSignOut()
    {
        var signedIn = SignedIn("user@test.test");
        WithHttpContext(signedIn);
        Assert.That((await _provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated, Is.True);

        // Sign out: the context now carries an anonymous principal. Without dropping the cache the
        // component tree would keep rendering as the previous user.
        AuthenticationState? published = null;
        _provider.AuthenticationStateChanged += task => published = task.Result;
        WithHttpContext(new ClaimsPrincipal(new ClaimsIdentity()));
        _provider.NotifyAuthenticationStateChanged();

        Assert.That(published, Is.Not.Null);
        Assert.That(published!.User.Identity!.IsAuthenticated, Is.False);
        Assert.That((await _provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated, Is.False);
    }
}
