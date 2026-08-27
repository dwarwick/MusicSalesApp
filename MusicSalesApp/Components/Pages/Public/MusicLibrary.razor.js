import { attach as attachHls, detach as detachHls, effectiveDuration } from '/js/hls-player.js';

// MusicLibrary card audio player module
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

// Map of audio elements by cardId for tracking multiple audio players
const cardPlayers = new Map();

// Stream tracking state (per card)
let STREAM_THRESHOLD_SECONDS = 30;
const MAX_TIME_DELTA_SECONDS = 1; // Maximum expected time between timeupdate events

export function initCardAudioPlayer(audioElement, cardId, dotNetRef, isRestricted = false, maxDuration = 60, songMetadataId = 0, streamThresholdSeconds = 30, trackLengthSeconds = 0) {
    if (!audioElement) return;

    // Update stream threshold from server-provided value
    STREAM_THRESHOLD_SECONDS = streamThresholdSeconds;

    // Store reference with restriction state and stream tracking
    cardPlayers.set(cardId, {
        trackLengthSeconds,
        audioElement, 
        dotNetRef, 
        isRestricted, 
        maxDuration,
        // Stream tracking state
        songMetadataId: songMetadataId,
        playedTime: 0,
        lastTime: 0,
        hasRecordedStream: false,
        isSeeking: false,
        hasReachedLimit: false
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
            if (!player.hasReachedLimit) {
                player.hasReachedLimit = true;
                dotNetRef.invokeMethodAsync('CardAudioEnded', cardId);
            }
            return;
        }

        // Track continuous playback time for stream counting
        if (player && !player.isSeeking && !player.hasRecordedStream && player.songMetadataId > 0) {
            const timeDelta = audioElement.currentTime - player.lastTime;
            // Clamped, not discarded - see the same block in SongPlayerInteractive.razor.js. A
            // timeupdate gap of a second or more used to drop that playback entirely, so the counter
            // ran behind real time and the configured threshold arrived late.
            if (timeDelta > 0) {
                player.playedTime += Math.min(timeDelta, MAX_TIME_DELTA_SECONDS);

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

    // Reports the SONG length, not the media element's. On a free preview the manifest is
    // truncated to 60s, so the element would report a one-minute song and the preview marker would
    // sit at the far right of the bar - telling the listener the opposite of what it means.
    const reportDuration = () => {
        // Read from the player state rather than the init closure: changeTrack updates it, and a
        // closure would keep reporting the length of whichever song this card started with.
        const current = cardPlayers.get(cardId);
        const duration = effectiveDuration(audioElement, current ? current.trackLengthSeconds : trackLengthSeconds);
        if (duration > 0) {
            dotNetRef.invokeMethodAsync('UpdateCardDuration', cardId, duration);
        }
    };

    audioElement.addEventListener('durationchange', reportDuration);
    audioElement.addEventListener('loadedmetadata', reportDuration);

    audioElement.addEventListener('ended', () => {
        dotNetRef.invokeMethodAsync('CardAudioEnded', cardId);
    });

    // Set initial volume from saved preference
    audioElement.volume = getSavedVolume();

    // Report the duration if the element already has it. There is deliberately no load() in the
    // else branch any more: at init there is no source yet, so it did nothing, and if init ever ran
    // after a source was attached it would reset the element and tear down hls.js's MediaSource.
    // Duration arrives either way - hls.js raises loadedmetadata once it has parsed the manifest.
    reportDuration();
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
        saveVolume(audioElement.volume);
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
        let newTime = effectiveDuration(audioElement, player ? player.trackLengthSeconds : 0) * percentage;
        
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
            let newTime = effectiveDuration(audioElement, player ? player.trackLengthSeconds : 0) * percentage;
            
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
            saveVolume(percentage);
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
        // attach() disposes any previous hls.js instance first. Cards reuse one audio element as
        // the listener moves between them, so without that each card left a live player behind.
        // No load() here: attach() does it on the native path, and calling it after attachMedia()
        // would tear down the MediaSource hls.js just attached.
        attachHls(audioElement, src);
    }
}

// Change the track source for album playback (used when transitioning to next track)
// isRestricted parameter updates the player state for the new track
// songMetadataId updates the stream tracking for the new track
export function changeTrack(audioElement, newSrc, cardId, isRestricted = null, songMetadataId = 0, trackLengthSeconds = 0) {
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
            player.hasReachedLimit = false;

            // The new track has its own length; keeping the previous one would mislabel it and
            // put the preview marker in the wrong place.
            player.trackLengthSeconds = trackLengthSeconds;
        }

        // Pause and reset first
        audioElement.pause();
        audioElement.currentTime = 0;
        
        // Set new source. attach() disposes the outgoing hls.js instance first.
        // Set new source. attach() disposes the outgoing hls.js instance and, on the native path,
        // calls load() itself - doing it here would tear down the MediaSource on the hls.js path.
        attachHls(audioElement, newSrc);
        
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

// ---------------------------------------------------------------------------
// Lazy card animations
//
// A song with no cover art shows a static glyph rendered server-side, and gets the
// real Lottie only while its card is actually on screen. Mounting one per art-less
// card is what filled the network tab with identical requests and left 25+ canvases,
// WASM renderer instances and animation loops running at once - the grid renders the
// whole filtered library with no virtualisation, so every art-less song paid.
//
// Everything here is client-side on purpose. Driving it from Blazor would mean an
// interop round trip per scroll event over the circuit, and the grid re-renders on
// every playback progress tick, so a refresh-on-render approach would be far worse
// than the problem it solves. A MutationObserver picks up cards added by a filter
// change instead.
// ---------------------------------------------------------------------------

let lottieVisibility = null;   // IntersectionObserver - which cards are on screen
let lottieGridWatcher = null;  // MutationObserver - cards added/removed by filtering
let lottieWatchedGrid = null;  // the element lottieGridWatcher is bound to
let lottieSrc = null;
let lottieRescanHandle = 0;

// Start a little before a card scrolls in, so the animation is already running by the
// time it is actually visible rather than popping in at the edge.
const LOTTIE_ROOT_MARGIN = '200px';

function mountLottie(host) {
    if (!lottieSrc || host.childElementCount > 0) return;

    const player = document.createElement('dotlottie-wc');
    player.setAttribute('src', lottieSrc);
    player.setAttribute('autoplay', '');
    player.setAttribute('loop', '');
    player.className = 'card-lottie-animation';

    host.appendChild(player);
    host.parentElement?.classList.add('is-animating');
}

function unmountLottie(host) {
    if (host.childElementCount === 0) return;

    // Drop the element entirely rather than pausing it: a paused player still holds a
    // canvas and its renderer, which is most of what we are trying not to pay for.
    host.replaceChildren();
    host.parentElement?.classList.remove('is-animating');
}

function scanLottieHosts() {
    if (!lottieVisibility) return;
    // observe() on an element already being observed is a no-op, so re-scanning after a
    // filter change is safe and needs no bookkeeping of its own.
    document.querySelectorAll('[data-lottie-host]').forEach(h => lottieVisibility.observe(h));
}

function scheduleLottieRescan() {
    clearTimeout(lottieRescanHandle);
    lottieRescanHandle = setTimeout(scanLottieHosts, 50);
}

// Returns true once it is actually attached, so the caller can stop retrying. The grid
// lives inside the page's loading/error branch, so it does not exist on first render.
export function initLazyCardAnimations() {
    const grid = document.querySelector('[data-lottie-grid]');
    if (!grid) return false;

    lottieSrc = grid.dataset.lottieSrc || null;
    if (!lottieSrc) return false;

    // No IntersectionObserver means no animation - the server-rendered glyph simply
    // stands, which is a perfectly good card rather than a broken one. Report success so
    // the caller stops asking; there is nothing here that retrying would fix.
    if (typeof IntersectionObserver === 'undefined' || typeof MutationObserver === 'undefined') {
        return true;
    }

    if (!lottieVisibility) {
        lottieVisibility = new IntersectionObserver(entries => {
            for (const entry of entries) {
                if (entry.isIntersecting) mountLottie(entry.target);
                else unmountLottie(entry.target);
            }
        }, { rootMargin: LOTTIE_ROOT_MARGIN });
    }

    // Re-attach if the grid we were watching has been swapped out. That branch is an
    // if/else, so going back through the loading or error state replaces the element and
    // would otherwise leave the watcher bound to a detached node.
    if (lottieGridWatcher && lottieWatchedGrid !== grid) {
        lottieGridWatcher.disconnect();
        lottieGridWatcher = null;
    }

    if (!lottieGridWatcher) {
        lottieGridWatcher = new MutationObserver(scheduleLottieRescan);
        lottieGridWatcher.observe(grid, { childList: true, subtree: true });
        lottieWatchedGrid = grid;
    }

    scanLottieHosts();
    return true;
}

export function disposeLazyCardAnimations() {
    clearTimeout(lottieRescanHandle);

    lottieGridWatcher?.disconnect();
    lottieGridWatcher = null;
    lottieWatchedGrid = null;

    lottieVisibility?.disconnect();
    lottieVisibility = null;

    document.querySelectorAll('[data-lottie-host]').forEach(unmountLottie);
    lottieSrc = null;
}

/**
 * Releases the hls.js instance attached to this element.
 *
 * The library reuses a single audio element as the listener moves between cards, so this is called
 * when the page goes away rather than per card - attach() handles the per-card teardown.
 */
export function disposeAudioPlayer(audioElement) {
    detachHls(audioElement);
}

/**
 * Surfaces a server-side "there is nothing to play" decision in the browser console.
 *
 * The server knows why a track has no URL; the browser is where anyone debugging playback is
 * actually looking. Without this the two never meet, and the only visible symptom is a
 * NotSupportedError from play() that says nothing about the cause.
 */
export function reportNoSource(reason) {
    console.error('[hls] ' + reason + ' Nothing was attached, so playback was not attempted. '
        + 'Check the server log for "has no encrypted HLS package" or "No metadata id for".');
}
