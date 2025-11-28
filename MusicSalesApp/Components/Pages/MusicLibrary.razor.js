// MusicLibrary card audio player module

// Map of audio elements by cardId for tracking multiple audio players
const cardPlayers = new Map();

export function initCardAudioPlayer(audioElement, cardId, dotNetRef) {
    if (!audioElement) return;

    // Store reference
    cardPlayers.set(cardId, { audioElement, dotNetRef });

    audioElement.addEventListener('timeupdate', () => {
        dotNetRef.invokeMethodAsync('UpdateCardTime', cardId, audioElement.currentTime);
    });

    audioElement.addEventListener('durationchange', () => {
        if (!isNaN(audioElement.duration) && isFinite(audioElement.duration)) {
            dotNetRef.invokeMethodAsync('UpdateCardDuration', cardId, audioElement.duration);
        }
    });

    audioElement.addEventListener('loadedmetadata', () => {
        if (!isNaN(audioElement.duration) && isFinite(audioElement.duration)) {
            dotNetRef.invokeMethodAsync('UpdateCardDuration', cardId, audioElement.duration);
        }
    });

    audioElement.addEventListener('ended', () => {
        dotNetRef.invokeMethodAsync('CardAudioEnded', cardId);
    });

    // Force load the metadata if not already loaded
    if (audioElement.readyState >= 1 && !isNaN(audioElement.duration) && isFinite(audioElement.duration)) {
        dotNetRef.invokeMethodAsync('UpdateCardDuration', cardId, audioElement.duration);
    } else {
        audioElement.load();
    }
}

export function playCard(audioElement) {
    if (audioElement) {
        audioElement.play().catch(err => console.warn('Play failed:', err));
    }
}

export function pauseCard(audioElement) {
    if (audioElement) {
        audioElement.pause();
    }
}

export function stopCard(audioElement) {
    if (audioElement) {
        audioElement.pause();
        audioElement.currentTime = 0;
    }
}

export function setCardVolume(audioElement, volume) {
    if (audioElement) {
        audioElement.volume = Math.max(0, Math.min(1, volume));
    }
}

export function setCardMuted(audioElement, muted) {
    if (audioElement) {
        audioElement.muted = muted;
    }
}

export function getElementWidth(element) {
    if (element) {
        return element.offsetWidth;
    }
    return 0;
}

export function seekCardToPosition(audioElement, offsetX, progressBarWidth) {
    if (audioElement && progressBarWidth > 0) {
        const percentage = offsetX / progressBarWidth;
        const newTime = audioElement.duration * percentage;
        if (!isNaN(newTime) && isFinite(newTime)) {
            audioElement.currentTime = newTime;
        }
    }
}

// Setup progress bar drag functionality for card player
export function setupCardProgressBarDrag(progressBarContainer, audioElement, cardId, dotNetRef) {
    if (!progressBarContainer || !audioElement) return;

    let isDragging = false;

    const updateSeekPosition = (clientX) => {
        const rect = progressBarContainer.getBoundingClientRect();
        const offsetX = clientX - rect.left;
        const width = rect.width;
        if (width > 0) {
            const percentage = Math.max(0, Math.min(1, offsetX / width));
            const newTime = audioElement.duration * percentage;
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

// Setup volume bar drag functionality for card player
export function setupCardVolumeBarDrag(volumeBarContainer, audioElement, cardId, dotNetRef) {
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
            dotNetRef.invokeMethodAsync('UpdateCardVolume', cardId, volume, false);
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

export function cleanupCardPlayer(cardId) {
    cardPlayers.delete(cardId);
}
