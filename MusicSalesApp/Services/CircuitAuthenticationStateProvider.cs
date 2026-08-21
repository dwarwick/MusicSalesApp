#nullable enable

using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MusicSalesApp.Services;

/// <summary>
/// Supplies the signed-in user to Razor components, in <b>both</b> render modes.
///
/// <para>
/// THE BUG THIS REPLACES. The previous implementation resolved the user by reading
/// <c>IHttpContextAccessor.HttpContext</c> and nothing else. That works during static SSR and
/// prerendering, where the request is still in scope - but once a Blazor Server circuit is
/// running there is <b>no HttpContext</b>, so it fell through to its anonymous branch and every
/// interactive component was told nobody was signed in.
/// </para>
///
/// <para>
/// It looked intermittent rather than broken because whether a context happened to be in scope
/// depended on how you arrived. Refreshing a page ran <c>OnAfterRenderAsync</c> inside the
/// circuit with no context, so the user read as anonymous; clicking a nav link used enhanced
/// navigation, which fetches the page over a real GET that <i>does</i> carry a context, so the
/// same code saw the user. That is why the home page's playlists appeared on a nav click and
/// vanished on refresh.
/// </para>
///
/// <para>
/// The fix is <see cref="IHostEnvironmentAuthenticationStateProvider"/>. Blazor's circuit
/// infrastructure resolves that interface when a circuit starts and hands it the user from the
/// connection, which is the supported way for an identity to survive into interactive code. The
/// HttpContext read is kept purely as the static-SSR path.
/// </para>
///
/// <para>
/// Renamed from <c>ServerAuthenticationStateProvider</c> deliberately: that is also the name of
/// the framework type it was standing in for, and the collision is a large part of why a custom
/// reimplementation went unnoticed.
/// </para>
/// </summary>
public sealed class CircuitAuthenticationStateProvider
    : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CircuitAuthenticationStateProvider> _logger;

    // volatile: written from hub threads (SetAuthenticationState at circuit start) and read
    // from renderer threads. A Task reference is atomic either way; volatile makes the
    // publish visible without relying on incidental synchronization.
    private volatile Task<AuthenticationState>? _authenticationStateTask;

    public CircuitAuthenticationStateProvider(
        IHttpContextAccessor httpContextAccessor,
        ILogger<CircuitAuthenticationStateProvider> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Called by the Blazor Server infrastructure as a circuit starts, with the user taken from
    /// the connection. This is what carries the identity past the end of the HTTP request.
    /// </summary>
    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
    {
        ArgumentNullException.ThrowIfNull(authenticationStateTask);

        _authenticationStateTask = authenticationStateTask;

        // One line per circuit start. This is the beacon that proves the running process has the
        // fix: hard-refresh any interactive page and this line must appear in the server console,
        // carrying the signed-in user's name. If it does not appear, the process is running old
        // code - no amount of browser cache clearing changes that.
        if (authenticationStateTask.IsCompletedSuccessfully)
        {
            var identity = authenticationStateTask.Result.User.Identity;
            _logger.LogInformation(
                "Circuit authentication handover: user={UserName}, authenticated={IsAuthenticated}",
                string.IsNullOrEmpty(identity?.Name) ? "(no name claim)" : identity!.Name,
                identity?.IsAuthenticated == true);
        }

        NotifyAuthenticationStateChanged(_authenticationStateTask);
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_authenticationStateTask is not null)
        {
            return _authenticationStateTask;
        }

        // Static SSR and prerendering: the request is still in scope. Cache the result so the
        // identity survives even if the context goes away later in this same scope.
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            _authenticationStateTask = Task.FromResult(new AuthenticationState(user));
            return _authenticationStateTask;
        }

        // Deliberately NOT cached. There may be no context yet simply because the circuit has not
        // called SetAuthenticationState; caching anonymous here would make that call arrive too
        // late and reintroduce the original bug.
        return Task.FromResult(Anonymous);
    }

    /// <summary>
    /// Re-reads and re-publishes the identity, so components re-render after a sign-in or
    /// sign-out. Called by <see cref="AuthenticationService"/>.
    ///
    /// <para>
    /// When an HttpContext is in scope (sign-in/out over HTTP), it is the truth - including when
    /// it says anonymous. When there is NO context, this is running inside a circuit, and the
    /// circuit-supplied identity is deliberately KEPT: nulling it would republish anonymous and
    /// visually sign the user out, which is the original bug in a new hat. An in-circuit
    /// sign-out cannot rewrite the auth cookie anyway, so it has to end in a full reload - and
    /// the fresh circuit receives the new identity through SetAuthenticationState.
    /// </para>
    /// </summary>
    public void NotifyAuthenticationStateChanged()
    {
        var httpUser = _httpContextAccessor.HttpContext?.User;
        if (httpUser is not null)
        {
            _authenticationStateTask = httpUser.Identity?.IsAuthenticated == true
                ? Task.FromResult(new AuthenticationState(httpUser))
                : null;
        }

        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
