/*
 * The editor's own audio transport.
 *
 * Deliberately its own module rather than a reach into the song page's: this player has no preview
 * restriction (it is the creator's own song), no previous/next (there is one song), and a playback
 * rate control the other players have no use for. Sharing would have meant parameterising all three
 * into a file two live pages depend on, to serve one page that wants something simpler.
 *
 * Everything here is at human speed - a click, a keypress - so the round trip into .NET is fine. The
 * per-frame work is the scroller's, and lives in its own module.
 */

let state = null;

/*
 * THE SAME KEY THE SONG AND PLAYLIST PLAYERS USE, deliberately. A creator who has set a comfortable
 * volume once has set it for this site, and a page that ignored that and opened at full every time
 * is worse here than anywhere else: this is the page they open to listen closely, often on
 * headphones, and the first thing it did was play at maximum.
 */
const VOLUME_STORAGE_KEY = 'streamtunes_volume';

function getSavedVolume() {
    try {
        const saved = localStorage.getItem(VOLUME_STORAGE_KEY);
        if (saved === null) {
            return 1;
        }

        const value = parseFloat(saved);
        return Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 1;
    } catch {
        // Private browsing. Full volume is the browser's own default, so this is no worse than
        // having never stored anything.
        return 1;
    }
}

function saveVolume(volume) {
    try {
        localStorage.setItem(VOLUME_STORAGE_KEY, volume.toString());
    } catch {
        /* Private browsing. The session still works; the preference just will not outlive it. */
    }
}

export function init(audio, dotNetRef, progressBar, volumeBar) {
    if (!audio) {
        return;
    }

    dispose(audio);

    state = { audio, dotNetRef, listeners: [] };

    const on = (target, event, handler) => {
        target.addEventListener(event, handler);
        state.listeners.push([target, event, handler]);
    };

    // ~4 Hz, which is all a numeric time display and a progress bar need. The word highlighting does
    // NOT come through here - it runs in the scroller's own animation-frame loop, because at this
    // rate on a Server circuit it would be visibly late and jerky.
    on(audio, 'timeupdate', () => dotNetRef.invokeMethodAsync('UpdateTime', audio.currentTime));
    on(audio, 'durationchange', () => dotNetRef.invokeMethodAsync('UpdateDuration', audio.duration || 0));
    on(audio, 'loadedmetadata', () => dotNetRef.invokeMethodAsync('UpdateDuration', audio.duration || 0));
    on(audio, 'play', () => dotNetRef.invokeMethodAsync('UpdatePlaying', true));
    on(audio, 'pause', () => dotNetRef.invokeMethodAsync('UpdatePlaying', false));
    on(audio, 'ended', () => dotNetRef.invokeMethodAsync('UpdatePlaying', false));
    on(audio, 'volumechange', () => {
        saveVolume(audio.volume);
        dotNetRef.invokeMethodAsync('UpdateVolume', audio.volume, audio.muted);
    });

    state.progressBar = progressBar;
    state.volumeBar = volumeBar;
    state.recording = false;

    // Before any listener can fire, so the restore is not itself broadcast as a change the creator
    // made. The volumechange handler above still saves it back, which is harmless - it writes the
    // value it just read.
    audio.volume = getSavedVolume();

    /*
     * DRAGGABLE, not just clickable. Both bars used to seek only on click, which on this page is the
     * wrong interaction: tapping a chorus means finding the same eight bars over and over, and
     * hunting for them one click at a time is exactly the fiddly part.
     *
     * Pointer events rather than the mouse/touch pair the other players use, because
     * setPointerCapture keeps the drag alive once the pointer leaves the bar - which it always does,
     * since these bars are a few pixels tall.
     */
    const wireBar = (container, apply) => {
        if (!container) {
            return;
        }

        let dragging = false;

        const ratioAt = (event) => {
            const rect = container.getBoundingClientRect();
            return Math.min(1, Math.max(0, (event.clientX - rect.left) / (rect.width || 1)));
        };

        on(container, 'pointerdown', (event) => {
            dragging = true;
            container.setPointerCapture?.(event.pointerId);
            apply(ratioAt(event));
            event.preventDefault();
        });

        on(container, 'pointermove', (event) => {
            if (dragging) {
                apply(ratioAt(event));
            }
        });

        on(container, 'pointerup', (event) => {
            dragging = false;
            container.releasePointerCapture?.(event.pointerId);
        });

        on(container, 'pointercancel', () => { dragging = false; });
    };

    wireBar(progressBar, (ratio) => {
        if (audio.duration) {
            audio.currentTime = ratio * audio.duration;
        }
    });

    wireBar(volumeBar, (ratio) => {
        audio.volume = ratio;
        audio.muted = ratio === 0;
    });

    /*
     * KEYBOARD HANDLING LIVES HERE, NOT IN BLAZOR, AND THAT IS THE WHOLE POINT OF TAP-ALONG.
     *
     * An @onkeydown on a Server circuit is a network round trip before any C# runs. At 120-250 ms
     * that is not a small inaccuracy - it is most of a syllable, and it varies with the connection,
     * so the creator would be calibrating against their own latency rather than against the song.
     *
     * What matters is that `audio.currentTime` is read in the SAME handler that saw the keypress.
     * Once that number exists, how long it takes to reach .NET is irrelevant.
     */
    on(document, 'keydown', (event) => {
        // Never steal a key from something the creator is typing into.
        const active = document.activeElement;
        if (active && (active.tagName === 'INPUT'
            || active.tagName === 'TEXTAREA'
            || active.isContentEditable)) {
            return;
        }

        if (event.code === 'Space') {
            // Without this the page scrolls, and any focused button fires again - so the creator's
            // first tap would also press whatever they clicked to start recording.
            event.preventDefault();

            if (state.recording) {
                dotNetRef.invokeMethodAsync('RecordLineTap', audio.currentTime * 1000);
            } else {
                audio.paused ? play(audio) : pause(audio);
            }
            return;
        }

        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
            event.preventDefault();
            dotNetRef.invokeMethodAsync('NudgeSelectedFromKeyboard', event.key === 'ArrowLeft' ? -1 : 1);
            return;
        }

        if (event.key === 'Escape') {
            event.preventDefault();

            // Stopping a tap pass outranks dropping a selection, since a pass is the mode you can be
            // stuck in. With no pass running, Escape means "I am done with that word".
            dotNetRef.invokeMethodAsync(
                state.recording ? 'StopRecordingFromKeyboard' : 'ClearSelectionFromKeyboard');
        }
    });

    // The on-screen Tap button, delegated so it works whenever the button appears and disappears
    // with record mode. Same reasoning as the key: the moment is read here, in the handler that saw
    // the click, so the trip to .NET afterwards costs nothing in accuracy.
    on(document, 'click', (event) => {
        if (!state.recording) {
            return;
        }

        if (event.target.closest('[data-tap-now]')) {
            dotNetRef.invokeMethodAsync('RecordLineTap', audio.currentTime * 1000);
        }
    });
}

/** Arm or disarm the tap pass. Held in JS so the keydown handler need not ask .NET what mode it is in. */
export function setRecording(recording) {
    if (state) {
        state.recording = recording;
    }
}

export function play(audio) {
    audio?.play?.().catch(() => { /* autoplay policy; the creator can press it again */ });
}

export function pause(audio) {
    audio?.pause?.();
}

/** Stop means back to the beginning, which is what a creator re-running a pass wants. */
export function stop(audio) {
    if (!audio) return;
    audio.pause();
    audio.currentTime = 0;
}

export function setMuted(audio, muted) {
    if (audio) audio.muted = muted;
}

export function setRate(audio, rate) {
    // preservesPitch keeps half speed sounding like the song rather than a tape slowing down, which
    // matters when the creator is listening for exactly where a word lands.
    if (!audio) return;
    audio.preservesPitch = true;
    audio.playbackRate = rate;
}

// Push the new position to .NET rather than waiting for the next `timeupdate`. Browsers fire that
// event about four times a second, so the elapsed readout would otherwise sit up to 250 ms stale
// after a seek - and the moment straight after a deliberate seek is precisely when the creator is
// reading it. Cheap, because a seek is a thing a person does, not something that fires on a clock.
function reportTime(audio) {
    if (state && state.dotNetRef && audio) {
        state.dotNetRef.invokeMethodAsync('UpdateTime', audio.currentTime);
    }
}

export function seekToMs(audio, ms) {
    if (!audio) return;
    audio.currentTime = ms / 1000;
    reportTime(audio);
}

export function seekToPosition(audio, container, offsetX) {
    if (!audio || !container || !audio.duration) return;
    const width = container.clientWidth || 1;
    const ratio = Math.min(1, Math.max(0, offsetX / width));
    audio.currentTime = ratio * audio.duration;
    reportTime(audio);
}

export function setVolumeFromPosition(audio, container, offsetX) {
    if (!audio || !container) return;
    const width = container.clientWidth || 1;
    const ratio = Math.min(1, Math.max(0, offsetX / width));
    audio.volume = ratio;
    audio.muted = ratio === 0;
}

export function dispose(audio) {
    if (!state) return;

    state.listeners.forEach(([target, event, handler]) => target.removeEventListener(event, handler));
    state.listeners = [];
    state = null;
}
