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

// Shared helper function to calculate clamped percentage from offset position
function calculatePercentage(clientX, element) {
    const rect = element.getBoundingClientRect();
    const offsetX = clientX - rect.left;
    const width = rect.width;
    if (width > 0) {
        return Math.max(0, Math.min(1, offsetX / width));
    }
    return null;
}

// Shared helper function to setup drag functionality on a bar element
function setupBarDrag(barContainer, onDrag) {
    if (!barContainer) return;

    let isDragging = false;

    barContainer.addEventListener('mousedown', (e) => {
        isDragging = true;
        onDrag(e.clientX);
        e.preventDefault();
    });

    document.addEventListener('mousemove', (e) => {
        if (isDragging) {
            onDrag(e.clientX);
        }
    });

    document.addEventListener('mouseup', () => {
        isDragging = false;
    });

    // Touch support for mobile
    barContainer.addEventListener('touchstart', (e) => {
        isDragging = true;
        if (e.touches.length > 0) {
            onDrag(e.touches[0].clientX);
        }
        e.preventDefault();
    });

    document.addEventListener('touchmove', (e) => {
        if (isDragging && e.touches.length > 0) {
            onDrag(e.touches[0].clientX);
        }
    });

    document.addEventListener('touchend', () => {
        isDragging = false;
    });
}

// Setup progress bar drag functionality for card player
export function setupCardProgressBarDrag(progressBarContainer, audioElement, cardId, dotNetRef) {
    if (!progressBarContainer || !audioElement) return;

    setupBarDrag(progressBarContainer, (clientX) => {
        const percentage = calculatePercentage(clientX, progressBarContainer);
        if (percentage !== null) {
            const newTime = audioElement.duration * percentage;
            if (!isNaN(newTime) && isFinite(newTime)) {
                audioElement.currentTime = newTime;
            }
        }
    });
}

// Setup volume bar drag functionality for card player
export function setupCardVolumeBarDrag(volumeBarContainer, audioElement, cardId, dotNetRef) {
    if (!volumeBarContainer || !audioElement) return;

    setupBarDrag(volumeBarContainer, (clientX) => {
        const percentage = calculatePercentage(clientX, volumeBarContainer);
        if (percentage !== null) {
            audioElement.volume = percentage;
            audioElement.muted = false;
            dotNetRef.invokeMethodAsync('UpdateCardVolume', cardId, percentage, false);
        }
    });
}

export function cleanupCardPlayer(cardId) {
    cardPlayers.delete(cardId);
}

// Change the track source for album playback (used when transitioning to next track)
export function changeTrack(audioElement, newSrc) {
    if (audioElement) {
        // Pause and reset first
        audioElement.pause();
        audioElement.currentTime = 0;
        
        // Set new source
        audioElement.src = newSrc;
        
        // Load and play
        audioElement.load();
        
        // Wait for the audio to be ready before playing
        const playWhenReady = () => {
            audioElement.play().catch(err => {
                console.warn('Play after track change failed:', err);
            });
        };
        
        // If ready state is sufficient, play immediately
        if (audioElement.readyState >= 2) {
            playWhenReady();
        } else {
            // Otherwise wait for canplay event
            audioElement.addEventListener('canplay', playWhenReady, { once: true });
        }
    }
}
