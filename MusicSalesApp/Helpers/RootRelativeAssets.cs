using Microsoft.AspNetCore.Components;

namespace MusicSalesApp.Helpers;

/// <summary>
/// Turns the document-relative URL that <see cref="ResourceAssetCollection"/> returns into a
/// root-relative one.
///
/// <para>
/// <c>@Assets["js/dashboard-helper.js"]</c> resolves to <c>js/dashboard-helper.x7hsw76v7g.js</c> -
/// no leading slash. A URL like that is resolved by the browser against the document's base URL, so
/// it is only correct while <c>&lt;base href="/"&gt;</c> is honoured. When it is not, the browser
/// falls back to the document's own directory, and on a two-segment route such as
/// <c>/song/{SongTitle}</c> that directory is <c>/song/</c> rather than <c>/</c>.
/// </para>
///
/// <para>
/// The failure mode is total, not partial. On 2026-08-31 production served six page views where
/// every script in the document was requested one directory too deep and returned 404:
/// <c>/song/_framework/blazor.web.wyu7y4jcvb.js</c>, <c>/song/_content/Syncfusion.Blazor/scripts/
/// syncfusion-blazor.min.js</c>, <c>/song/lib/hls/hls.min.syz84jjn22.js</c> and the rest. Because
/// <c>blazor.web.js</c> was among them the circuit never started, so nothing was logged beyond the
/// 404s themselves - the visitor got a page with no interactivity and the server recorded no error.
/// Six of 505 song and artist page views that day, and invisible until someone read the access log.
/// </para>
///
/// <para>
/// Note this is exactly why the incident is confined to <c>/song/{SongTitle}</c> and
/// <c>/artist/{ArtistName}</c>: they are the only public two-segment routes. On <c>/</c>,
/// <c>/login</c> or <c>/about</c> the document's directory <em>is</em> <c>/</c>, so a
/// document-relative URL happens to resolve correctly and the defect stays hidden.
/// </para>
///
/// <para>
/// Prefer this over a hand-written <c>"/@Assets[...]"</c> at the call site. Both produce the same
/// markup, but the interpolation form silently degrades to a document-relative URL if someone drops
/// the slash, which is the bug this exists to prevent.
/// </para>
/// </summary>
public static class RootRelativeAssets
{
    /// <summary>
    /// Returns the fingerprinted URL for <paramref name="key"/> with a leading slash, so the
    /// browser resolves it against the origin instead of the current document.
    /// </summary>
    /// <remarks>
    /// A resolved URL that is already absolute is returned untouched. That covers an asset served
    /// from another origin (<c>https://cdn.example/app.css</c>) and the protocol-relative
    /// (<c>//cdn.example/app.css</c>) form, neither of which is resolved against the document.
    /// </remarks>
    public static string Root(this ResourceAssetCollection assets, string key)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var resolved = assets[key];

        if (string.IsNullOrEmpty(resolved))
        {
            return "/";
        }

        if (resolved.StartsWith('/') || Uri.IsWellFormedUriString(resolved, UriKind.Absolute))
        {
            return resolved;
        }

        return "/" + resolved;
    }
}
