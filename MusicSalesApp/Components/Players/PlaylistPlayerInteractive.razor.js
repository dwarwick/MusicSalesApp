// Volume persistence via localStorage
const VOLUME_STORAGE_KEY = 'streamtunes_volume';
const DEFAULT_VOLUME = 0.4;

function saveVolume(volume) {
    try { localStorage.setItem(VOLUME_STORAGE_KEY, volume.toString()); } catch (e) { console.warn('Failed to save volume:', e); }
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
    hasReachedLimit: false
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

export function initAudioPlayer(audioElement, dotNetRef, isRestricted = false, maxDuration = 60, songMetadataId = 0, streamThresholdSeconds = 30) {
    audioElement = resolveAudioElement(audioElement);
    if (!audioElement) return;

    // Store initial state
    playerState.isRestricted = isRestricted;
    playerState.maxDuration = maxDuration;
    playerState.hasReachedLimit = false;

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
            // Only count if time moved forward naturally (not seeking)
            if (timeDelta > 0 && timeDelta < MAX_TIME_DELTA_SECONDS) {
                streamTracker.playedTime += timeDelta;
                
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

    audioElement.addEventListener('durationchange', () => {
        if (!isNaN(audioElement.duration) && isFinite(audioElement.duration)) {
            dotNetRef.invokeMethodAsync('UpdateDuration', audioElement.duration);
        }
    });

    audioElement.addEventListener('loadedmetadata', () => {
        if (!isNaN(audioElement.duration) && isFinite(audioElement.duration)) {
            dotNetRef.invokeMethodAsync('UpdateDuration', audioElement.duration);
        }
    });

    audioElement.addEventListener('ended', () => {
        dotNetRef.invokeMethodAsync('AudioEnded');
    });

    // Set initial volume from saved preference
    audioElement.volume = getSavedVolume();

    // Force load the metadata if not already loaded
    if (audioElement.readyState >= 1 && !isNaN(audioElement.duration) && isFinite(audioElement.duration)) {
        dotNetRef.invokeMethodAsync('UpdateDuration', audioElement.duration);
    } else {
        // Trigger metadata load
        audioElement.load();
    }
}

// Update the restriction state (called when track ownership changes)
export function updateRestrictionState(isRestricted) {
    playerState.isRestricted = isRestricted;
    playerState.hasReachedLimit = false;
}

export function play(audioElement) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
        audioElement.play().catch(err => console.warn('Play failed:', err));
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
        let newTime = audioElement.duration * percentage;

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
        audioElement.src = src;
        audioElement.load();
    }
}

// Change the track source for album playback (used when transitioning to next/previous track)
// isRestricted parameter updates the player state for the new track
// songMetadataId updates the stream tracking for the new track
export function changeTrack(audioElement, newSrc, isRestricted = null, songMetadataId = 0, streamThresholdSeconds = null) {
    audioElement = resolveAudioElement(audioElement);
    if (audioElement) {
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

        // Set new source
        audioElement.src = newSrc;

        // Load and play
        audioElement.load();

        const playWhenReady = () => {
            audioElement.play().catch(err => {
                console.warn('Play after track change failed:', err);
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
            let newTime = audioElement.duration * percentage;

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