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

export function initAudioPlayer(audioElement, dotNetRef, isRestricted = false, maxDuration = 60, songMetadataId = 0, streamThresholdSeconds = 30) {
    if (!audioElement) return;

    // Update stream threshold from server-provided value
    STREAM_THRESHOLD_SECONDS = streamThresholdSeconds;

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

export function play(audioElement) {
    if (audioElement) {
        audioElement.play().catch(err => console.warn('Play failed:', err));
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
    if (audioElement && !isNaN(audioElement.duration) && isFinite(audioElement.duration)) {
        return audioElement.duration;
    }
    return 0;
}

// Setup progress bar drag functionality
export function setupProgressBarDrag(progressBarContainer, audioElement, dotNetRef, isRestricted = false, maxDuration = 60) {
    if (!progressBarContainer || !audioElement) return;

    let isDragging = false;

    const updateSeekPosition = (clientX) => {
        const rect = progressBarContainer.getBoundingClientRect();
        const offsetX = clientX - rect.left;
        const width = rect.width;
        if (width > 0) {
            const percentage = Math.max(0, Math.min(1, offsetX / width));
            let newTime = audioElement.duration * percentage;
            
            // Enforce max duration limit for restricted users
            if (isRestricted && newTime > maxDuration) {
                newTime = maxDuration;
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
