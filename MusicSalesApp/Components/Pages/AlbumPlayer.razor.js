// State object to store restriction settings (can be updated when tracks change)
let playerState = {
    isRestricted: false,
    maxDuration: 60
};

export function initAudioPlayer(audioElement, dotNetRef, isRestricted = false, maxDuration = 60) {
    if (!audioElement) return;

    // Store initial state
    playerState.isRestricted = isRestricted;
    playerState.maxDuration = maxDuration;

    audioElement.addEventListener('timeupdate', () => {
        // Enforce 60 second limit for restricted users (uses current state)
        if (playerState.isRestricted && audioElement.currentTime >= playerState.maxDuration) {
            audioElement.pause();
            audioElement.currentTime = playerState.maxDuration;
            dotNetRef.invokeMethodAsync('AudioEnded');
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

// Set the track source without auto-playing (for initial load)
export function setTrackSource(audioElement, src) {
    if (audioElement && src) {
        audioElement.src = src;
        audioElement.load();
    }
}

// Change the track source for album playback (used when transitioning to next/previous track)
// isRestricted parameter updates the player state for the new track
export function changeTrack(audioElement, newSrc, isRestricted = null) {
    if (audioElement) {
        // Update restriction state if provided
        if (isRestricted !== null) {
            playerState.isRestricted = isRestricted;
        }

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
    if (audioElement) {
        audioElement.volume = Math.max(0, Math.min(1, volume));
    }
}

export function getVolume(audioElement) {
    if (audioElement) {
        return audioElement.volume;
    }
    return 1;
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