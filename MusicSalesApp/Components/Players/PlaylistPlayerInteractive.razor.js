import { attach as attachHls, detach as detachHls, effectiveDuration, logWarn } from '/js/hls-player.js';

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

// Helper to validate an element reference and fall back to getElementById.
// In Blazor Server, an ElementReference may arrive as an unresolved object
// (e.g. after a PayPal redirect) when the render batch hasn't been applied yet.
function resolveAudioElement(audioElement) {
    if (audioElement && typeof audioElement.addEventListener === 'function') {
        return audioElement;
    }
    return document.getElementById('playlist-audio-player');
}

// State object to store restriction settings (can be updated when tracks change)
let playerState = {
    isRestricted: false,
    maxDuration: 60,
    hasReachedLimit: false,

    // The current track's true length, from the server. A free-preview listener's manifest is
    // truncated, so the media element only knows about the first 60 seconds. Held per player rather
    // than per module because a playlist changes track without re-initialising.
    trackLengthSeconds: 0
};

// Stream tracking state for album player
let STREAM_THRESHOLD_SECONDS = 30;
const MAX_TIME_DELTA_SECONDS = 1; // Maximum expected time between timeupdate events
let streamTracker = {
    songMetadataId: 0,
    playedTime: 0,
    lastTime: 0,
    hasRecordedStream: false,
    isSeeking: false
};

export function initAudioPlayer(audioElement, dotNetRef, isRestricted = false, maxDuration = 60, songMetadataId = 0, streamThresholdSeconds = 30, trackLengthSeconds = 0) {
    audioElement = resolveAudioElement(audioElement);
    if (!audioElement) return;

    // Store initial state
    playerState.isRestricted = isRestricted;
    playerState.maxDuration = maxDuration;
    playerState.hasReachedLimit = false;
    playerState.trackLengthSeconds = trackLengthSeconds;

    // Update stream threshold from server-provided value
    STREAM_THRESHOLD_SECONDS = streamThresholdSeconds;

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
        // Enforce 60 second limit for restricted users (uses current state)
        if (playerState.isRestricted && audioElement.currentTime >= playerState.maxDuration) {
            audioElement.pause();
            audioElement.currentTime = playerState.maxDuration;
            if (!playerState.hasReachedLimit) {
                playerState.hasReachedLimit = true;
                dotNetRef.invokeMethodAsync('AudioEnded');
            }
        }

        // Track continuous playback time for stream counting
        if (!streamTracker.isSeeking && !streamTracker.hasRecordedStream && streamTracker.songMetadataId > 0) {
            const timeDelta = audioElement.currentTime - streamTracker.lastTime;
            // Clamped, not discarded - see the same block in SongPlayerInteractive.razor.js. A
            // timeupdate gap of a second or more used to drop that playback entirely, so the counter
            // ran behind real time and the configured threshold arrived late.
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
        const duration = effectiveDuration(audioElement, playerState.trackLengthSeconds);
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

// updateRestrictionState() was removed with the redesign: it was exported but never invoked
// from C#. changeTrack() already refreshes playerState.isRestricted on every track change,
// which is the only moment the restriction can differ.

export function play(audioElement) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
        audioElement.play().catch(err => logWarn('Play failed:', err));
    }
}

export function pause(audioElement) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
        audioElement.pause();
    }
}

export function seekTo(audioElement, time) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
        audioElement.currentTime = time;
    }
}

export function seekToPosition(audioElement, offsetX, progressBarWidth, isRestricted = false, maxDuration = 60) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement && progressBarWidth > 0) {
        const percentage = offsetX / progressBarWidth;
        let newTime = effectiveDuration(audioElement, playerState.trackLengthSeconds) * percentage;

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
    audioElement = resolveAudioElement(audioElement);
    if (audioElement && !isNaN(audioElement.duration) && isFinite(audioElement.duration)) {
        return audioElement.duration;
    }
    return 0;
}

// Set the track source without auto-playing (for initial load)
export function setTrackSource(audioElement, src) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement && src) {
        // attach() tears down any previous hls.js instance before creating the next one. A
        // playlist changes source on every track, so without that a thirty-track playlist ends up
        // with thirty live players each holding a worker and a fetch loop.
        // No load() here: attach() does it on the native path, and calling it after attachMedia()
        // would tear down the MediaSource hls.js just attached.
        attachHls(audioElement, src);
    }
}

// Change the track source for album playback (used when transitioning to next/previous track)
// isRestricted parameter updates the player state for the new track
// songMetadataId updates the stream tracking for the new track
export function changeTrack(audioElement, newSrc, isRestricted = null, songMetadataId = 0, streamThresholdSeconds = null, trackLengthSeconds = 0) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
        // The new track has its own length; keeping the previous one would mislabel it and put the
        // preview marker in the wrong place.
        playerState.trackLengthSeconds = trackLengthSeconds;

        // Update restriction state if provided
        if (isRestricted !== null) {
            playerState.isRestricted = isRestricted;
        }
        playerState.hasReachedLimit = false;

        // Update stream threshold if provided
        if (streamThresholdSeconds !== null) {
            STREAM_THRESHOLD_SECONDS = streamThresholdSeconds;
        }

        // Reset stream tracking for the new track
        streamTracker = {
            songMetadataId: songMetadataId,
            playedTime: 0,
            lastTime: 0,
            hasRecordedStream: false,
            isSeeking: false
        };

        // Pause and reset first
        audioElement.pause();
        audioElement.currentTime = 0;

        // Set new source. attach() disposes the outgoing hls.js instance first.
        // Set new source. attach() disposes the outgoing hls.js instance and, on the native path,
        // calls load() itself - doing it here would tear down the MediaSource on the hls.js path.
        attachHls(audioElement, newSrc);

        const playWhenReady = () => {
            audioElement.play().catch(err => {
                logWarn('Play after track change failed:', err);
            });
        };

        if (audioElement.readyState >= 2) {
            playWhenReady();
        } else {
            audioElement.addEventListener('canplay', playWhenReady, { once: true });
        }
    }
}

// Setup progress bar drag functionality
// Note: Uses playerState for restriction checking to stay in sync with current track
export function setupProgressBarDrag(progressBarContainer, audioElement, dotNetRef) {
    audioElement = resolveAudioElement(audioElement);
    if (!progressBarContainer || !audioElement) return;

    let isDragging = false;

    const updateSeekPosition = (clientX) => {
        const rect = progressBarContainer.getBoundingClientRect();
        const offsetX = clientX - rect.left;
        const width = rect.width;
        if (width > 0) {
            const percentage = Math.max(0, Math.min(1, offsetX / width));
            let newTime = effectiveDuration(audioElement, playerState.trackLengthSeconds) * percentage;

            // Enforce max duration limit for restricted users (uses current state)
            if (playerState.isRestricted && newTime > playerState.maxDuration) {
                newTime = playerState.maxDuration;
            }

            if (!isNaN(newTime) && isFinite(newTime)) {
                audioElement.currentTime = newTime;
            }
        }
    };

    progressBarContainer.addEventListener('mousedown', (e) => {
        isDragging = true;
        updateSeekPosition(e.clientX);
        e.preventDefault();
    });

    document.addEventListener('mousemove', (e) => {
        if (isDragging) {
            updateSeekPosition(e.clientX);
        }
    });

    document.addEventListener('mouseup', () => {
        isDragging = false;
    });

    // Touch support for mobile
    progressBarContainer.addEventListener('touchstart', (e) => {
        isDragging = true;
        if (e.touches.length > 0) {
            updateSeekPosition(e.touches[0].clientX);
        }
        e.preventDefault();
    });

    document.addEventListener('touchmove', (e) => {
        if (isDragging && e.touches.length > 0) {
            updateSeekPosition(e.touches[0].clientX);
        }
    });

    document.addEventListener('touchend', () => {
        isDragging = false;
    });
}

// Volume control functions
export function setVolume(audioElement, volume) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
        audioElement.volume = Math.max(0, Math.min(1, volume));
        saveVolume(audioElement.volume);
    }
}

export function getVolume(audioElement) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
        return audioElement.volume;
    }
    return DEFAULT_VOLUME;
}

export function setMuted(audioElement, muted) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
        audioElement.muted = muted;
    }
}

export function isMuted(audioElement) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
        return audioElement.muted;
    }
    return false;
}

// Setup volume bar drag functionality
export function setupVolumeBarDrag(volumeBarContainer, audioElement, dotNetRef) {
    audioElement = resolveAudioElement(audioElement);
    if (!volumeBarContainer || !audioElement) return;

    let isDragging = false;

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

    volumeBarContainer.addEventListener('mousedown', (e) => {
        isDragging = true;
        updateVolume(e.clientX);
        e.preventDefault();
    });

    document.addEventListener('mousemove', (e) => {
        if (isDragging) {
            updateVolume(e.clientX);
        }
    });

    document.addEventListener('mouseup', () => {
        isDragging = false;
    });

    // Touch support for mobile
    volumeBarContainer.addEventListener('touchstart', (e) => {
        isDragging = true;
        if (e.touches.length > 0) {
            updateVolume(e.touches[0].clientX);
        }
        e.preventDefault();
    });

    document.addEventListener('touchmove', (e) => {
        if (isDragging && e.touches.length > 0) {
            updateVolume(e.touches[0].clientX);
        }
    });

    document.addEventListener('touchend', () => {
        isDragging = false;
    });
}
/**
 * Releases the hls.js instance attached to this element.
 *
 * Called from the component's DisposeAsync. Matters more here than on the single-song player: a
 * playlist attaches a new instance per track, so leaving the last one alive leaves a worker and a
 * segment fetch loop running after the page is gone.
 */
export function disposeAudioPlayer(audioElement) {
    detachHls(resolveAudioElement(audioElement));
}
