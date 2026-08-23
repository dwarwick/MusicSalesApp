// Reconnect handling for the Blazor Server circuit.
//
// Two constraints shape this file.
//
// 1. NOTHING HERE MAY EXIST IN THE SERVER-RENDERED HTML. <ReconnectModal /> sits in App.razor, so it
//    ships on every page; the framework's stock wording was being picked up as Google snippets and
//    made a healthy site look broken. Every string below is written into the DOM at runtime, on an
//    event a crawler never triggers. The markup ships as empty spans - see ReconnectModal.razor,
//    and wwwroot/seo-hide-framework-ui.css, which hides these elements while they are :empty.
//
// 2. RECONNECTING IS NEVER BEHIND A CLICK, AND NEVER GIVES UP. Reconnecting is lossless: the circuit
//    resumes with all component state intact. Reloading is not - it throws away pasted lyrics, an
//    upload form, a playing song. This used to give up after three attempts (~17s) and reload, which
//    during a deploy produced reload -> fresh page -> failure -> reload, an invisible loop that hit
//    the server hardest exactly while it was coming back up. Now it retries forever with a capped
//    backoff, and reloads only when the server has told us the circuit is genuinely unrecoverable.

const reconnectModal = document.getElementById("components-reconnect-modal");
const pill = document.getElementById("st-reconnect-pill");
const pillText = pill ? pill.querySelector(".st-reconnect-text") : null;

// Written in at runtime, never in the markup. See note 1 above.
const MESSAGES = {
    reconnecting: "Reconnecting…",
    lost: "Connection lost — reloading…"
};

// Capped backoff. The tail matters more than the head: a deploy or a laptop waking from sleep is
// measured in tens of seconds, not in three attempts.
const RETRY_DELAYS = [2000, 5000, 10000, 20000, 30000];
const RELOAD_NOTICE_MS = 2000;

let attempt = 0;
let retryTimeout = null;
let retryInFlight = false;
let reloading = false;
let waitingForVisible = false;

// Connection health monitoring (optional logging - development only)
let lastActivityTime = Date.now();
let connectionHealthInterval = null;
let isDevelopment = false;

try {
    isDevelopment = window.location.hostname === "localhost" ||
        window.location.hostname === "127.0.0.1" ||
        window.location.hostname.includes("local") ||
        document.documentElement.hasAttribute("data-development");
} catch (e) {
    // If detection fails, assume production (safer default)
    isDevelopment = false;
}

function log(...args) {
    if (isDevelopment) {
        console.log(...args);
    }
}

// ------------------------------------------------------------------ the pill

function showPill(message, terminal) {
    if (!pill || !pillText) {
        return;
    }

    pillText.textContent = message;
    pill.classList.toggle("st-reconnect-terminal", terminal === true);
    pill.classList.add("st-reconnect-visible");
}

function hidePill() {
    if (!pill || !pillText) {
        return;
    }

    pill.classList.remove("st-reconnect-visible", "st-reconnect-terminal");

    // Emptied again so the :empty rules in seo-hide-framework-ui.css keep applying, and so a page
    // saved or scraped after a recovered blip carries no reconnect wording either.
    pillText.textContent = "";
}

// Blazor opens its dialog with showModal(), which makes the rest of the document inert even though
// our CSS never renders it. Closing it keeps the page selectable - somebody watching their
// connection drop with pasted lyrics on screen needs to be able to copy them out.
function closeFrameworkDialog() {
    if (!reconnectModal) {
        return;
    }

    try {
        if (reconnectModal.open) {
            reconnectModal.close();
        }
    } catch (e) {
        // Nothing here is worth breaking reconnection over.
    }
}

// ------------------------------------------------------------------ retry loop

function cancelScheduledRetry() {
    if (retryTimeout) {
        clearTimeout(retryTimeout);
        retryTimeout = null;
    }
}

function onRecovered() {
    log("Reconnection successful.");
    cancelScheduledRetry();
    attempt = 0;
    retryInFlight = false;
    waitingForVisible = false;
    hidePill();
    closeFrameworkDialog();
}

function reloadWithNotice(reason) {
    if (reloading) {
        return;
    }

    reloading = true;
    cancelScheduledRetry();
    log(`Circuit unrecoverable (${reason}). Reloading…`);

    // The only case where a reload is the honest answer: the server has told us this circuit no
    // longer exists, so no amount of retrying can bring the page's state back. The notice is not a
    // prompt and there is nothing to click - it is there so the page does not blank unexplained.
    showPill(MESSAGES.lost, true);
    setTimeout(() => location.reload(), RELOAD_NOTICE_MS);
}

function scheduleRetry() {
    cancelScheduledRetry();

    if (reloading) {
        return;
    }

    // A background tab cannot show anybody anything, and burning retries in one is how a laptop
    // comes back from sleep to a page that has already given up. Wait for it to be looked at.
    if (document.visibilityState === "hidden") {
        waitingForVisible = true;
        log("Tab hidden; holding reconnection until it is visible again.");
        return;
    }

    const delay = RETRY_DELAYS[Math.min(attempt, RETRY_DELAYS.length - 1)];
    attempt++;
    log(`Scheduling reconnection attempt ${attempt} in ${delay}ms…`);

    retryTimeout = setTimeout(retry, delay);
}

async function retry() {
    retryTimeout = null;

    if (reloading || retryInFlight) {
        return;
    }

    retryInFlight = true;

    try {
        // Blazor.reconnect() resolves true on success, false when the server was reached but the
        // circuit is gone, and throws when the server could not be reached at all. Only the middle
        // case is terminal - an exception just means "not yet".
        const successful = await Blazor.reconnect();

        if (successful) {
            onRecovered();
            return;
        }

        if (typeof Blazor.resumeCircuit === "function") {
            const resumed = await Blazor.resumeCircuit();

            if (resumed) {
                onRecovered();
                return;
            }
        }

        reloadWithNotice("circuit could not be resumed");
    } catch (err) {
        // Always logged, even in production: a reconnect loop nobody can see is how this went
        // unnoticed the first time.
        console.error("Reconnect attempt failed:", err);
        scheduleRetry();
    } finally {
        retryInFlight = false;
    }
}

function retryNow() {
    if (reloading || retryInFlight) {
        return;
    }

    cancelScheduledRetry();
    retry();
}

// ------------------------------------------------------------------ Blazor's state events

function handleReconnectStateChanged(event) {
    updateLastActivity();

    const state = event.detail.state;

    // Whatever the state, we never want the framework's modal holding the document inert. Also on a
    // timeout, in case the dialog is opened after the event rather than before it.
    closeFrameworkDialog();
    setTimeout(closeFrameworkDialog, 0);

    if (state === "show") {
        // Blazor is running its own reconnection attempts. Say so and let it work.
        log("Connection lost. Blazor is attempting to reconnect…");
        showPill(MESSAGES.reconnecting);
    } else if (state === "hide") {
        onRecovered();
    } else if (state === "failed") {
        // Blazor has given up; we have not. The pill deliberately keeps saying "Reconnecting" -
        // showing a failure through attempts that are still very likely to succeed reads worse than
        // showing nothing at all.
        log("Blazor reconnection failed. Continuing with our own retries…");
        showPill(MESSAGES.reconnecting);
        attempt = 0;
        scheduleRetry();
    } else if (state === "rejected") {
        // The server reached us and refused: this circuit is gone for good.
        reloadWithNotice("rejected by the server");
    } else if (state === "resume-failed") {
        reloadWithNotice("resume failed");
    } else if (state === "paused") {
        showPill(MESSAGES.reconnecting);
    }
}

if (reconnectModal) {
    reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
}

// Come back the moment there is any reason to think it might work now.
window.addEventListener("online", () => {
    if (waitingForVisible || retryTimeout || attempt > 0) {
        log("Browser reports it is back online; retrying immediately.");
        attempt = 0;
        retryNow();
    }
});

document.addEventListener("visibilitychange", () => {
    if (document.visibilityState !== "visible") {
        return;
    }

    updateLastActivity();

    if (waitingForVisible) {
        waitingForVisible = false;
        log("Tab visible again; resuming reconnection.");
        retryNow();
    }
});

// ------------------------------------------------------------------ dev-only health logging

function startConnectionHealthMonitor() {
    if (!isDevelopment) return; // Only run in development
    if (connectionHealthInterval) return;

    connectionHealthInterval = setInterval(() => {
        const timeSinceLastActivity = Date.now() - lastActivityTime;

        if (timeSinceLastActivity < 65000) { // 65 seconds (slightly more than client timeout)
            console.log(`[SignalR Health] Connection active. Last activity: ${Math.floor(timeSinceLastActivity / 1000)}s ago`);
        } else {
            console.warn(`[SignalR Health] No activity for ${Math.floor(timeSinceLastActivity / 1000)}s. Connection may be stale.`);
        }
    }, 30000); // Check every 30 seconds
}

function updateLastActivity() {
    lastActivityTime = Date.now();
}

function setupActivityTracking() {
    const interactionEvents = ["click", "keydown", "touchstart", "scroll"];
    interactionEvents.forEach(eventType => {
        document.addEventListener(eventType, updateLastActivity, { passive: true });
    });

    document.addEventListener("DOMContentLoaded", updateLastActivity);
}

if (typeof Blazor !== "undefined") {
    startConnectionHealthMonitor();
    if (isDevelopment) {
        console.log("[SignalR Health] Connection health monitoring started. Logs will appear every 30 seconds.");
    }

    setupActivityTracking();
}
