// MusicLibrary card audio player module

// Map of audio elements by cardId for tracking multiple audio players
const cardPlayers = new Map();

// Stream tracking state (per card)
const STREAM_THRESHOLD_SECONDS = 30;

export function initCardAudioPlayer(audioElement, cardId, dotNetRef, isRestricted = false, maxDuration = 60, songMetadataId = 0) {
    if (!audioElement) return;

    // Store reference with restriction state and stream tracking
    cardPlayers.set(cardId, { 
        audioElement, 
        dotNetRef, 
        isRestricted, 
        maxDuration,
        // Stream tracking state
        songMetadataId: songMetadataId,
        playedTime: 0,
        lastTime: 0,
        hasRecordedStream: false,
        isSeeking: false
    });

    // Track seeking events to reset continuous playback tracking
    audioElement.addEventListener('seeking', () => {
        const player = cardPlayers.get(cardId);
        if (player) {
            player.isSeeking = true;
        }
    });

    audioElement.addEventListener('seeked', () => {
        const player = cardPlayers.get(cardId);
        if (player) {
            // Reset the continuous playback counter when user seeks
            player.playedTime = 0;
            player.lastTime = audioElement.currentTime;
            player.isSeeking = false;
        }
    });

    audioElement.addEventListener('timeupdate', () => {
        const player = cardPlayers.get(cardId);
        // Enforce 60 second limit for restricted users
        if (player && player.isRestricted && audioElement.currentTime >= player.maxDuration) {
            audioElement.pause();
            audioElement.currentTime = player.maxDuration;
            dotNetRef.invokeMethodAsync('CardAudioEnded', cardId);
            return;
        }

        // Track continuous playback time for stream counting
        if (player && !player.isSeeking && !player.hasRecordedStream && player.songMetadataId > 0) {
            const timeDelta = audioElement.currentTime - player.lastTime;
            // Only count if time moved forward naturally (not seeking)
            if (timeDelta > 0 && timeDelta < 1) {
                player.playedTime += timeDelta;
                
                // Check if we've reached the threshold
                if (player.playedTime >= STREAM_THRESHOLD_SECONDS) {
                    player.hasRecordedStream = true;
                    dotNetRef.invokeMethodAsync('RecordStream', player.songMetadataId);
                }
            }
            player.lastTime = audioElement.currentTime;
        }

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

export function seekCardToPosition(audioElement, offsetX, progressBarWidth, cardId) {
    if (audioElement && progressBarWidth > 0) {
        const player = cardPlayers.get(cardId);
        const percentage = offsetX / progressBarWidth;
        let newTime = audioElement.duration * percentage;
        
        // Enforce max duration limit for restricted users
        if (player && player.isRestricted && newTime > player.maxDuration) {
            newTime = player.maxDuration;
        }
        
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
        const player = cardPlayers.get(cardId);
        const percentage = calculatePercentage(clientX, progressBarContainer);
        if (percentage !== null) {
            let newTime = audioElement.duration * percentage;
            
            // Enforce max duration limit for restricted users
            if (player && player.isRestricted && newTime > player.maxDuration) {
                newTime = player.maxDuration;
            }
            
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

// Set the track source without auto-playing (for initial load)
export function setTrackSource(audioElement, src) {
    if (audioElement && src) {
        audioElement.src = src;
        audioElement.load();
    }
}

// Change the track source for album playback (used when transitioning to next track)
// isRestricted parameter updates the player state for the new track
// songMetadataId updates the stream tracking for the new track
export function changeTrack(audioElement, newSrc, cardId, isRestricted = null, songMetadataId = 0) {
    if (audioElement) {
        // Update restriction state and reset stream tracking if provided
        const player = cardPlayers.get(cardId);
        if (player) {
            if (isRestricted !== null) {
                player.isRestricted = isRestricted;
            }
            // Reset stream tracking for the new track
            player.songMetadataId = songMetadataId;
            player.playedTime = 0;
            player.lastTime = 0;
            player.hasRecordedStream = false;
            player.isSeeking = false;
        }

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
