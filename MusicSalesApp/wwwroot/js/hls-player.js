// Attaches an audio element to an encrypted-HLS manifest.
//
// Every player on the site goes through here rather than assigning `audio.src` directly, because
// "set the source" is no longer one operation. Safari plays an .m3u8 natively; every other browser
// needs hls.js and an attached Hls instance whose lifetime has to be managed, and getting that
// wrong leaks a worker plus a network loop per track change on a playlist.
//
// The manifest itself is per-listener and short-lived server-side: it names only the segments that
// listener is entitled to, and its EXT-X-KEY URI carries a token good for about a minute. Nothing
// here needs to know that - it just fetches what it is given, same-origin, with the cookie the
// browser already sends.

const instances = new WeakMap();

/**
 * Whether to narrate playback to the console.
 *
 * Set by App.razor from the server's hosting environment: on everywhere except Production. Read
 * through a function rather than captured once, because this module is imported before the players
 * run and reading it lazily keeps it honest if the flag is ever set later.
 */
function isVerbose() {
    return typeof window !== 'undefined' && window.__stVerbosePlayback === true;
}

/**
 * Console helpers for everything that is diagnostic rather than a fault.
 *
 * Exported so the three player modules share one switch. Only info and warn are gated: an error
 * means playback is broken, and a listener's console is the only place that report can come from.
 */
export function logInfo(...args) {
    if (isVerbose()) {
        console.info(...args);
    }
}

export function logWarn(...args) {
    if (isVerbose()) {
        console.warn(...args);
    }
}

/**
 * True when the browser can play HLS without help. Safari and iOS WebViews can; nothing else can.
 */
function canPlayNatively(audioElement) {
    return audioElement.canPlayType('application/vnd.apple.mpegurl') !== '';
}

/**
 * Tears down any hls.js instance previously attached to this element.
 *
 * Called before every attach and on dispose. hls.js holds a worker, buffered segments and an
 * in-flight fetch loop; without this a playlist of thirty tracks ends up with thirty live players
 * all still polling.
 */
export function detach(audioElement) {
    if (!audioElement) {
        return;
    }

    const existing = instances.get(audioElement);
    if (existing) {
        existing.destroy();
        instances.delete(audioElement);
    }
}

/**
 * Points an audio element at a manifest URL.
 *
 * @param audioElement the <audio> element
 * @param manifestUrl  the tokenised manifest URL from the server
 * @returns true when a source was attached, false when there was nothing to attach
 */
export function attach(audioElement, manifestUrl) {
    if (!audioElement) {
        console.error('[hls] attach called with no audio element.');
        return false;
    }

    // Loud, because the alternative is silent. When the server cannot build a manifest URL it says
    // so in ITS log, and the browser sees only a later "no supported source was found" from play()
    // — an error that describes the symptom and hides the cause.
    if (!manifestUrl) {
        console.error(
            '[hls] No manifest URL for this track, so nothing was attached. The song most likely '
            + 'has no encrypted HLS package yet. Check the server log for "has no encrypted HLS '
            + 'package" or "No metadata id for".');
        return false;
    }

    detach(audioElement);

    // hls.js FIRST, native second — the reverse of the obvious order, and deliberate.
    //
    // canPlayType('application/vnd.apple.mpegurl') is the standard native-HLS probe, but it is a
    // hint rather than a guarantee: a browser that answers "maybe" and then cannot actually demux
    // the playlist leaves the element with networkState NO_SOURCE, and play() rejects with
    // NotSupportedError — the symptom being debugged here, with nothing in the console to say why.
    //
    // Preferring hls.js wherever MSE exists sends every desktop browser down one well-tested path
    // and removes any dependence on that probe being honest. Native is then reached only where MSE
    // genuinely is absent, which in practice means iOS Safari — the one place native HLS is both
    // required and known-good.
    if (typeof Hls !== 'undefined' && Hls.isSupported()) {
        return attachViaHlsJs(audioElement, manifestUrl);
    }

    if (canPlayNatively(audioElement)) {
        logInfo('[hls] Using native HLS playback (no MSE available).');
        audioElement.src = manifestUrl;

        // Correct on this branch only, and why callers must not call load() themselves: on the
        // hls.js branch attachMedia() points the element at a MediaSource object URL, and load()
        // would reset the element and tear that MediaSource straight back down. hls.js would carry
        // on fetching manifest, key and segments regardless — its loader is independent of the
        // element — so the network tab would look perfect while play() rejected.
        audioElement.load();
        return true;
    }

    console.error(
        '[hls] This browser can play neither MSE nor native HLS, so encrypted audio cannot play. '
        + 'hls.js loaded: ' + (typeof Hls !== 'undefined') + '.');
    return false;
}

/**
 * Attaches through hls.js and Media Source Extensions.
 */
function attachViaHlsJs(audioElement, manifestUrl) {

    const hls = new Hls({
        // The catalogue is music, not video: there is one rendition and no bandwidth ladder to
        // climb, so the defaults tuned for adaptive video only add latency to the first note.
        enableWorker: true,
        lowLatencyMode: false,

        // Enough to ride out a brief network stall without buffering minutes of a song the
        // listener may skip in ten seconds.
        maxBufferLength: 60,

        // Same-origin, so the browser attaches the auth cookie itself. Deliberately no xhrSetup:
        // the credential is already in the manifest URL or the cookie, and a second mechanism
        // would be a second thing to get wrong.
        xhrSetup: undefined
    });

    // Guards the credential-refresh path below against spinning. Reset once anything buffers
    // successfully, so a long listening session can recover more than once.
    let refreshAttempts = 0;
    let refreshing = false;

    /**
     * Re-fetches the manifest to obtain fresh credentials, and resumes where playback was.
     *
     * The segment SAS and the key token both expire, and the manifest is where both are handed out
     * — the server stamps a new SAS and mints a new key token every time it builds one. But the
     * manifest is VOD with an ENDLIST, so the player fetches it once and never asks again: there is
     * no natural moment for it to pick up fresh credentials. Without this, an expired SAS ends
     * playback permanently, and hls.startLoad() only retries the same dead URL.
     *
     * Position is saved and restored because loadSource() restarts the playlist, which would
     * otherwise silently jump the listener back to the beginning of the track.
     */
    function refreshCredentialsAndResume(reason) {
        if (refreshing) {
            return;
        }

        // Two attempts, then stop. If fresh credentials do not fix it the fault is not expiry -
        // a revoked key, a deleted package, a wrong wrapping key - and retrying would hammer the
        // manifest endpoint for as long as the page stayed open.
        if (refreshAttempts >= 2) {
            console.error(
                '[hls] Still failing after refreshing credentials twice, so this is not an expiry. '
                + 'Giving up. Last reason:', reason);
            hls.destroy();
            instances.delete(audioElement);
            return;
        }

        refreshing = true;
        refreshAttempts++;

        const resumeAt = audioElement.currentTime;
        const wasPlaying = !audioElement.paused;

        logWarn(
            '[hls] Credentials expired (' + reason + '); refetching the manifest and resuming at '
            + resumeAt.toFixed(1) + 's.');

        hls.once(Hls.Events.MANIFEST_PARSED, () => {
            if (resumeAt > 0) {
                audioElement.currentTime = resumeAt;
            }

            if (wasPlaying) {
                audioElement.play().catch(err => {
                    // Expected whenever the listener pauses while the refresh is in flight: the
                    // pause interrupts the play() promise. Their intent wins, and it is not a fault.
                    if (err && err.name === 'AbortError') {
                        return;
                    }

                    logWarn('[hls] Resume after refresh failed:', err);
                });
            }

            refreshing = false;
        });

        // Tells hls.js where to begin loading the new playlist. Without it, loadSource() starts from
        // the top and re-fetches segments the listener is nowhere near - two minutes into a track it
        // pulled seg-000 and seg-001 again before the seek took effect. The currentTime restore above
        // still happens, and is what actually moves playback; this only stops the wasted fetches.
        hls.config.startPosition = resumeAt;

        // The same URL: its own token lasts far longer than the segment SAS, and the endpoint
        // stamps fresh segment credentials on every request. That ordering is a real constraint —
        // see Hls:SegmentSasLifetime, which must stay below Hls:ManifestTokenLifetime or there is
        // no window in which this can work.
        hls.loadSource(manifestUrl);
    }

    hls.on(Hls.Events.ERROR, (_event, data) => {
        // details and response are what actually identify the fault - a 401 on the key, a 403 on a
        // segment SAS, an unsupported codec - and a message without them says only "something went
        // wrong", which is how a playback bug turns into an afternoon.
        const status = data.response ? data.response.code : null;
        const where = data.details + (status ? ' (HTTP ' + status + ')' : '');
        const isExpiredCredential = status === 401 || status === 403;

        // If the REFRESH is what failed, stop treating one as in flight. Otherwise the guard inside
        // refreshCredentialsAndResume blocks every future attempt and playback stays dead silently -
        // the one outcome worse than the expiry it was meant to fix.
        if (refreshing && typeof data.details === 'string' && data.details.indexOf('manifest') !== -1) {
            refreshing = false;
        }

        // Acted on BEFORE the fatal check, deliberately. 401 and 403 are verdicts, not blips: the
        // credential is dead and retrying the identical URL cannot succeed. Waiting for hls.js to
        // exhaust its retries and declare the error fatal cost six pointless requests and a audible
        // gap in testing. Refreshing on the first one recovers in a single round trip.
        if (isExpiredCredential) {
            refreshCredentialsAndResume(where);
            return;
        }

        if (!data.fatal) {
            return;
        }

        switch (data.type) {
            case Hls.ErrorTypes.NETWORK_ERROR:
                logWarn('[hls] Network error, retrying:', where, data);
                hls.startLoad();
                break;
            case Hls.ErrorTypes.MEDIA_ERROR:
                logWarn('[hls] Media error, attempting recovery:', where, data);
                hls.recoverMediaError();
                break;
            default:
                console.error('[hls] Unrecoverable error:', where, data);
                hls.destroy();
                instances.delete(audioElement);
                break;
        }
    });

    // Anything buffering proves the current credentials work, so a later expiry gets its own full
    // allowance of retries. Without this reset, a session long enough to expire twice would refuse
    // to recover the second time.
    hls.on(Hls.Events.FRAG_BUFFERED, () => {
        refreshAttempts = 0;
    });

    // Confirms which path was taken and that a source really was attached. Without it, "no
    // supported source was found" from play() is indistinguishable from attach never having run.
    hls.on(Hls.Events.MANIFEST_PARSED, (_event, data) => {
        logInfo('[hls] Manifest parsed:', data.levels?.length ?? 0, 'level(s); source attached.');
    });

    hls.loadSource(manifestUrl);
    hls.attachMedia(audioElement);
    instances.set(audioElement, hls);

    return true;
}

/**
 * The duration to DISPLAY, and to measure the progress bar and seeking against.
 *
 * For a listener on a free preview the manifest is truncated server-side, so the media element's
 * own duration really is 60 seconds — it has not been told the rest of the song exists. Using that
 * for display tells the listener a four-minute song is one minute long and pegs the preview marker
 * at the far right of the bar, which is exactly backwards: the marker exists to show how little of
 * the song the preview covers.
 *
 * The server's track length is therefore authoritative whenever it is known, and the element's own
 * duration is the fallback. Seeking must use the same number, or the bar and the audio disagree:
 * a click 25% along a four-minute bar has to mean 60s, not 15s.
 */
export function effectiveDuration(audioElement, trackLengthSeconds) {
    if (trackLengthSeconds > 0) {
        return trackLengthSeconds;
    }

    const own = audioElement ? audioElement.duration : NaN;
    return (!isNaN(own) && isFinite(own)) ? own : 0;
}
