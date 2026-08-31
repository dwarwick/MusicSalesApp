/*
 * Holding a link click until the page it is leaving says it may go.
 *
 * ---------------------------------------------------------------------------
 * WHY THIS EXISTS, AND WHY <NavigationLock> IS NOT ENOUGH ON ITS OWN
 * ---------------------------------------------------------------------------
 *
 * NavigationLock registers a location-changing handler on the circuit's NavigationManager, so it
 * only ever sees navigations the circuit itself performs - a NavigateTo call from C#. This app
 * mounts its router as a bare <Routes /> with NO @rendermode (see App.razor), so routing is static
 * SSR and interactivity is per-page. An ordinary <a href> is therefore handled by blazor.web.js's
 * enhanced navigation: it fetches the new page and patches the DOM without the circuit's
 * NavigationManager ever being asked for permission.
 *
 * The failure mode is silent. No error, no warning - the handler simply never runs, which is how a
 * publish guard on the lyrics editor shipped and let creators walk straight off the page, and how
 * the upload page's own lock came to warn on refresh (that is ConfirmExternalNavigation, a separate
 * beforeunload path that DOES still work) while letting a nav-menu click through untouched.
 *
 * So anchors are caught here instead, in the capture phase, before enhanced navigation gets the
 * click. Pages keep their NavigationLock as well: between them the two cover both ways off a page.
 *
 * ---------------------------------------------------------------------------
 * THE CONTRACT
 * ---------------------------------------------------------------------------
 *
 * arm() takes a DotNetObjectReference to a component exposing:
 *
 *     [JSInvokable] public Task<bool> RequestLeave()
 *
 * returning true to let the navigation proceed and false to stay put. The DECISION IS ALWAYS .NET'S
 * - this module reports where a click was going and does as it is told - so the rule about which
 * states are worth interrupting lives in one place per page rather than being split across two
 * languages.
 *
 * Note also that Syncfusion's <MenuItem Url="..."> renders an anchor, so the nav menu comes through
 * here like any other link.
 */

let guard = null;

/** Whether this click would actually take the browser somewhere, rather than opening a new tab. */
function isPlainLeftClick(event) {
    return event.button === 0
        && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey
        && !event.defaultPrevented;
}

/** The internal destination this click is heading for, or null if it is not one. */
function internalDestination(event) {
    const anchor = event.target.closest?.('a[href]');

    if (!anchor || anchor.hasAttribute('download')) {
        return null;
    }

    // _blank and friends leave this page open, so there is nothing to lose and nothing to ask about.
    const target = anchor.getAttribute('target');
    if (target && target !== '_self') {
        return null;
    }

    // Resolved rather than read, so a relative href is compared like for like. mailto:, tel: and
    // javascript: all fail the origin test below, which is why they need no special case.
    let url;
    try {
        url = new URL(anchor.href, document.baseURI);
    } catch {
        return null;
    }

    if (url.origin !== window.location.origin) {
        return null;
    }

    // A fragment on the page they are already on is not leaving.
    if (url.pathname === window.location.pathname && url.search === window.location.search) {
        return null;
    }

    return url.href;
}

/** Start asking before an anchor takes the visitor off this page. */
export function arm(dotNetRef) {
    disarm();

    const handler = async (event) => {
        if (!isPlainLeftClick(event)) {
            return;
        }

        const href = internalDestination(event);
        if (!href) {
            return;
        }

        // Held back rather than cancelled: if .NET says to go, this navigates to the same place a
        // moment later, and the visitor cannot tell the difference.
        event.preventDefault();
        event.stopPropagation();

        let mayLeave = true;

        try {
            mayLeave = await dotNetRef.invokeMethodAsync('RequestLeave');
        } catch {
            // A dropped circuit must never trap somebody on a page. The prompt is a courtesy; being
            // unable to navigate because the server went away is a fault.
            mayLeave = true;
        }

        if (mayLeave) {
            navigateTo(href);
        }
    };

    document.addEventListener('click', handler, true);
    guard = handler;
}

export function disarm() {
    if (guard) {
        document.removeEventListener('click', guard, true);
        guard = null;
    }
}

/** Enhanced navigation where it is available, a plain load where it is not. */
function navigateTo(href) {
    disarm();

    if (window.Blazor && typeof window.Blazor.navigateTo === 'function') {
        window.Blazor.navigateTo(href);
        return;
    }

    window.location.assign(href);
}
