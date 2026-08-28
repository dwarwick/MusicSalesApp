import { attach as attachHls, detach as detachHls, effectiveDuration, logWarn } from '/js/hls-player.js';
import { bindBarDrag, unbindBarDrag } from '/js/drag-bar.js';

// Volume persistence via localStorage
const VOLUME_STORAGE_KEY = 'streamtunes_volume';
const DEFAULT_VOLUME = 0.4;

function saveVolume(volume) {
    try { localStorage.setItem(VOLUME_STORAGE_KEY, volume.toString()); } catch (e) { logWarn('Failed to save volume:', e); }
}

export function getSavedVolume() {
    try {
        const saved = localStorage.getItem(VOLUME_STORAGE_KEY);
        if (saved !== null) {
            const vol = parseFloat(saved);
            if (!isNaN(vol) && vol >= 0 && vol <= 1) return vol;
        }
    } catch (e) { /* ignore */ }
    return DEFAULT_VOLUME;
}

// Stream tracking state
let STREAM_THRESHOLD_SECONDS = 30;
const MAX_TIME_DELTA_SECONDS = 1; // Maximum expected time between timeupdate events
let streamTracker = {
    songMetadataId: 0,
    playedTime: 0,
    lastTime: 0,
    hasRecordedStream: false,
    isSeeking: false
};

let hasReachedLimit = false;

// The song's true length, supplied by the server. A free-preview listener's manifest is truncated,
// so the media element only knows about the first 60 seconds and cannot be asked for this.
let trackLengthSeconds = 0;

export function initAudioPlayer(audioElement, dotNetRef, isRestricted = false, maxDuration = 60, songMetadataId = 0, streamThresholdSeconds = 30, manifestUrl = null, songLengthSeconds = 0) {
    if (!audioElement) return;

    // Attaching first is safe even though the listeners are wired below: the events this triggers
    // (loadedmetadata, durationchange) are asynchronous, so they cannot fire before this
    // synchronous function has finished registering for them.
    attachHls(audioElement, manifestUrl);

    // Update stream threshold from server-provided value
    STREAM_THRESHOLD_SECONDS = streamThresholdSeconds;
    trackLengthSeconds = songLengthSeconds;

    // Reset state for new song
    hasReachedLimit = false;

    // Reset stream tracking for new song
    streamTracker = {
        songMetadataId: songMetadataId,
        playedTime: 0,
        lastTime: 0,
        hasRecordedStream: false,
        isSeeking: false
    };

    // Track seeking events to reset continuous playback tracking
    audioElement.addEventListener('seeking', () => {
        streamTracker.isSeeking = true;
    });

    audioElement.addEventListener('seeked', () => {
        // Reset the continuous playback counter when user seeks
        streamTracker.playedTime = 0;
        streamTracker.lastTime = audioElement.currentTime;
        streamTracker.isSeeking = false;
    });

    audioElement.addEventListener('timeupdate', () => {
        // Enforce 60 second limit for non-owners
        if (isRestricted && audioElement.currentTime >= maxDuration) {
            audioElement.pause();
            audioElement.currentTime = maxDuration;
            if (!hasReachedLimit) {
                hasReachedLimit = true;
                dotNetRef.invokeMethodAsync('AudioEnded');
            }
        }

        // Track continuous playback time for stream counting
        if (!streamTracker.isSeeking && !streamTracker.hasRecordedStream && streamTracker.songMetadataId > 0) {
            const timeDelta = audioElement.currentTime - streamTracker.lastTime;
            // Count forward movement, clamped rather than discarded. timeupdate normally fires about
            // every 250ms, but the browser may fire it whenever it likes - a gap stretches past a
            // second on buffering, a GC pause, or a busy main thread. Dropping those gaps entirely
            // (the old `timeDelta < MAX` test) lost that playback for good, so the counter ran behind
            // real time and the threshold arrived several seconds late. Clamping keeps the original
            // intent - a stray forward jump contributes at most one second, and 'seeked' still resets
            // the counter outright - without leaking ordinary playback.
            if (timeDelta > 0) {
                streamTracker.playedTime += Math.min(timeDelta, MAX_TIME_DELTA_SECONDS);

                // Check if we've reached the threshold
                if (streamTracker.playedTime >= STREAM_THRESHOLD_SECONDS) {
                    streamTracker.hasRecordedStream = true;
                    dotNetRef.invokeMethodAsync('RecordStream', streamTracker.songMetadataId);
                }
            }
            streamTracker.lastTime = audioElement.currentTime;
        }

        dotNetRef.invokeMethodAsync('UpdateTime', audioElement.currentTime);
    });

    // Reports the SONG length, not the media element's. On a free preview the manifest is truncated
    // to 60s, so the element would report a one-minute song and the preview marker would sit at the
    // far right of the bar - telling the listener the opposite of what it means.
    const reportDuration = () => {
        const duration = effectiveDuration(audioElement, trackLengthSeconds);
        if (duration > 0) {
            dotNetRef.invokeMethodAsync('UpdateDuration', duration);
        }
    };

    audioElement.addEventListener('durationchange', reportDuration);
    audioElement.addEventListener('loadedmetadata', reportDuration);

    audioElement.addEventListener('ended', () => {
        dotNetRef.invokeMethodAsync('AudioEnded');
    });

    // Set initial volume from saved preference
    audioElement.volume = getSavedVolume();

    // Report the duration if the element already has it.
    //
    // The else branch used to call load(), and on this player that was actively harmful: attach()
    // runs at the top of this same function, so load() at the bottom reset the element and tore
    // down the MediaSource hls.js had just attached. hls.js keeps fetching the manifest, key and
    // segments regardless, so the network tab looks perfect while play() rejects with
    // NotSupportedError. Duration arrives without it - hls.js raises loadedmetadata once it has
    // parsed the manifest, and the native path calls load() inside attach().
    reportDuration();
}

export function play(audioElement) {
    if (audioElement) {
        audioElement.play().catch(err => logWarn('Play failed:', err));
    }
}

export function pause(audioElement) {
    if (audioElement) {
        audioElement.pause();
    }
}

export function seekTo(audioElement, time) {
    if (audioElement) {
        audioElement.currentTime = time;
    }
}

export function seekToPosition(audioElement, offsetX, progressBarWidth, isRestricted = false, maxDuration = 60) {
    if (audioElement && progressBarWidth > 0) {
        const percentage = offsetX / progressBarWidth;
        let newTime = effectiveDuration(audioElement, trackLengthSeconds) * percentage;
        
        // Enforce max duration limit for restricted users
        if (isRestricted && newTime > maxDuration) {
            newTime = maxDuration;
        }
        
        if (!isNaN(newTime) && isFinite(newTime)) {
            audioElement.currentTime = newTime;
        }
    }
}

export function getElementWidth(element) {
    if (element) {
        return element.offsetWidth;
    }
    return 0;
}

export function getDuration(audioElement) {
    return effectiveDuration(audioElement, trackLengthSeconds);
}

// Setup progress bar drag functionality
export function setupProgressBarDrag(progressBarContainer, audioElement, dotNetRef, isRestricted = false, maxDuration = 60) {
    if (!progressBarContainer || !audioElement) return;

    const updateSeekPosition = (clientX) => {
        const rect = progressBarContainer.getBoundingClientRect();
        const offsetX = clientX - rect.left;
        const width = rect.width;
        if (width > 0) {
            const percentage = Math.max(0, Math.min(1, offsetX / width));
            let newTime = effectiveDuration(audioElement, trackLengthSeconds) * percentage;
            
            // Enforce max duration limit for restricted users
            if (isRestricted && newTime > maxDuration) {
                newTime = maxDuration;
            }
            
            if (!isNaN(newTime) && isFinite(newTime)) {
                audioElement.currentTime = newTime;
            }
        }
    };

    bindBarDrag(progressBarContainer, updateSeekPosition);
}

// Volume control functions
export function setVolume(audioElement, volume) {
    if (audioElement) {
        audioElement.volume = Math.max(0, Math.min(1, volume));
        saveVolume(audioElement.volume);
    }
}

export function getVolume(audioElement) {
    if (audioElement) {
        return audioElement.volume;
    }
    return DEFAULT_VOLUME;
}

export function setMuted(audioElement, muted) {
    if (audioElement) {
        audioElement.muted = muted;
    }
}

export function isMuted(audioElement) {
    if (audioElement) {
        return audioElement.muted;
    }
    return false;
}

// Setup volume bar drag functionality
export function setupVolumeBarDrag(volumeBarContainer, audioElement, dotNetRef) {
    if (!volumeBarContainer || !audioElement) return;

    const updateVolume = (clientX) => {
        const rect = volumeBarContainer.getBoundingClientRect();
        const offsetX = clientX - rect.left;
        const width = rect.width;
        if (width > 0) {
            const volume = Math.max(0, Math.min(1, offsetX / width));
            audioElement.volume = volume;
            audioElement.muted = false;
            saveVolume(volume);
            dotNetRef.invokeMethodAsync('UpdateVolume', volume, false);
        }
    };

    bindBarDrag(volumeBarContainer, updateVolume);
}

/**
 * Releases the hls.js instance attached to this element.
 *
 * Called from the component's DisposeAsync. Without it, navigating between songs leaves a live
 * player per visited track - each holding a worker, buffered segments and a fetch loop.
 */
export function disposeAudioPlayer(audioElement) {
    detachHls(audioElement);
}

/**
 * Releases the progress and volume bar drag bindings.
 *
 * Four of each bar's six listeners live on `document`, so they outlive the bar element itself - and
 * in a Blazor SPA, navigating away never unloads the page that would otherwise have taken them with
 * it. Re-binding the same bar replaces its registration, but a bar belonging to a page the user has
 * left is never bound again, so it has to be released here.
 */
export function disposeBarDrags(progressBarContainer, volumeBarContainer) {
    unbindBarDrag(progressBarContainer);
    unbindBarDrag(volumeBarContainer);
}
