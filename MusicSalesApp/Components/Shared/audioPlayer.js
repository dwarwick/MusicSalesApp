// Shared audio player utilities for Blazor components

function clampVolume(volume) {
    return Math.max(0, Math.min(1, volume));
}

function isValidDuration(duration) {
    return !isNaN(duration) && isFinite(duration);
}

function calculatePercentage(clientX, element) {
    const rect = element.getBoundingClientRect();
    const offsetX = clientX - rect.left;
    const width = rect.width;
    if (width > 0) {
        return Math.max(0, Math.min(1, offsetX / width));
    }
    return null;
}

function setupBarDrag(barContainer, onDragStart) {
    if (!barContainer) return;

    let isDragging = false;

    const handlePosition = (clientX) => {
        onDragStart(clientX);
    };

    barContainer.addEventListener('mousedown', (e) => {
        isDragging = true;
        handlePosition(e.clientX);
        e.preventDefault();
    });

    document.addEventListener('mousemove', (e) => {
        if (isDragging) {
            handlePosition(e.clientX);
        }
    });

    document.addEventListener('mouseup', () => {
        isDragging = false;
    });

    // Touch support for mobile
    barContainer.addEventListener('touchstart', (e) => {
        isDragging = true;
        if (e.touches.length > 0) {
            handlePosition(e.touches[0].clientX);
        }
        e.preventDefault();
    });

    document.addEventListener('touchmove', (e) => {
        if (isDragging && e.touches.length > 0) {
            handlePosition(e.touches[0].clientX);
        }
    });

    document.addEventListener('touchend', () => {
        isDragging = false;
    });
}

function attachCommonAudioEvents(audioElement, callbacks, options = {}) {
    if (!audioElement) return;

    const {
        onTimeUpdate,
        onDuration,
        onEnded,
        onRestrictionReached
    } = callbacks;

    const {
        isRestricted = false,
        maxDuration = 60
    } = options;

    audioElement.addEventListener('timeupdate', () => {
        if (isRestricted && audioElement.currentTime >= maxDuration) {
            audioElement.pause();
            audioElement.currentTime = maxDuration;
            onRestrictionReached?.();
            onEnded?.();
            return;
        }
        onTimeUpdate?.(audioElement.currentTime);
    });

    const handleDurationUpdate = () => {
        if (isValidDuration(audioElement.duration)) {
            onDuration?.(audioElement.duration);
        }
    };

    audioElement.addEventListener('durationchange', handleDurationUpdate);
    audioElement.addEventListener('loadedmetadata', handleDurationUpdate);
    audioElement.addEventListener('ended', () => onEnded?.());

    if (audioElement.readyState >= 1 && isValidDuration(audioElement.duration)) {
        onDuration?.(audioElement.duration);
    } else {
        audioElement.load();
    }
}

export function initAudioPlayer(audioElement, callbacks, options = {}) {
    attachCommonAudioEvents(audioElement, callbacks, options);
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

export function stop(audioElement) {
    if (audioElement) {
        audioElement.pause();
        audioElement.currentTime = 0;
    }
}

export function seekTo(audioElement, time) {
    if (audioElement) {
        audioElement.currentTime = time;
    }
}

export function seekToPosition(audioElement, offsetX, progressBarWidth, options = {}) {
    const { isRestricted = false, maxDuration = 60 } = options;
    if (audioElement && progressBarWidth > 0) {
        const percentage = offsetX / progressBarWidth;
        let newTime = audioElement.duration * percentage;

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
    if (audioElement && isValidDuration(audioElement.duration)) {
        return audioElement.duration;
    }
    return 0;
}

export function setTrackSource(audioElement, src) {
    if (audioElement && src) {
        audioElement.src = src;
        audioElement.load();
    }
}

export function changeTrack(audioElement, newSrc) {
    if (!audioElement) return;

    audioElement.pause();
    audioElement.currentTime = 0;
    audioElement.src = newSrc;
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

export function setVolume(audioElement, volume) {
    if (audioElement) {
        audioElement.volume = clampVolume(volume);
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

export function setupProgressBarDrag(progressBarContainer, audioElement, options = {}) {
    if (!progressBarContainer || !audioElement) return;

    const { isRestricted = false, maxDuration = 60 } = options;

    setupBarDrag(progressBarContainer, (clientX) => {
        const percentage = calculatePercentage(clientX, progressBarContainer);
        if (percentage !== null) {
            let newTime = audioElement.duration * percentage;
            if (isRestricted && newTime > maxDuration) {
                newTime = maxDuration;
            }
            if (!isNaN(newTime) && isFinite(newTime)) {
                audioElement.currentTime = newTime;
            }
        }
    });
}

export function setupVolumeBarDrag(volumeBarContainer, audioElement, onVolumeChanged) {
    if (!volumeBarContainer || !audioElement) return;

    setupBarDrag(volumeBarContainer, (clientX) => {
        const percentage = calculatePercentage(clientX, volumeBarContainer);
        if (percentage !== null) {
            const volume = clampVolume(percentage);
            audioElement.volume = volume;
            audioElement.muted = false;
            onVolumeChanged?.(volume, false);
        }
    });
}

export function cleanupAudioPlayer(audioElement) {
    if (!audioElement) return;
    audioElement.pause();
    audioElement.src = '';
}
