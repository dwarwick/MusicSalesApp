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
    on(audio, 'volumechange', () => dotNetRef.invokeMethodAsync('UpdateVolume', audio.volume, audio.muted));

    state.progressBar = progressBar;
    state.volumeBar = volumeBar;
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

export function seekToMs(audio, ms) {
    if (audio) audio.currentTime = ms / 1000;
}

export function seekToPosition(audio, container, offsetX) {
    if (!audio || !container || !audio.duration) return;
    const width = container.clientWidth || 1;
    const ratio = Math.min(1, Math.max(0, offsetX / width));
    audio.currentTime = ratio * audio.duration;
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
