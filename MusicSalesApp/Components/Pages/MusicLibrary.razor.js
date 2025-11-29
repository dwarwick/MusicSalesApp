import {
    initAudioPlayer as initSharedAudio,
    play as playAudio,
    pause as pauseAudio,
    stop as stopAudio,
    setVolume,
    setMuted,
    getElementWidth,
    seekToPosition as seekToPositionWithLimit,
    setupProgressBarDrag as setupProgressDrag,
    setupVolumeBarDrag as setupVolumeDrag,
    changeTrack
} from '../Shared/audioPlayer.js';

// Map of audio elements by cardId for tracking multiple audio players
const cardPlayers = new Map();

export function initCardAudioPlayer(audioElement, cardId, dotNetRef, isRestricted = false, maxDuration = 60) {
    if (!audioElement) return;

    cardPlayers.set(cardId, { audioElement, dotNetRef, isRestricted, maxDuration });

    initSharedAudio(audioElement, {
        onTimeUpdate: (time) => dotNetRef.invokeMethodAsync('UpdateCardTime', cardId, time),
        onDuration: (duration) => dotNetRef.invokeMethodAsync('UpdateCardDuration', cardId, duration),
        onEnded: () => dotNetRef.invokeMethodAsync('CardAudioEnded', cardId),
        onRestrictionReached: () => dotNetRef.invokeMethodAsync('CardAudioEnded', cardId)
    }, {
        isRestricted,
        maxDuration
    });
}

export function playCard(audioElement) {
    playAudio(audioElement);
}

export function pauseCard(audioElement) {
    pauseAudio(audioElement);
}

export function stopCard(audioElement) {
    stopAudio(audioElement);
}

export function setCardVolume(audioElement, volume) {
    setVolume(audioElement, volume);
}

export function setCardMuted(audioElement, muted) {
    setMuted(audioElement, muted);
}

export { getElementWidth };

export function seekCardToPosition(audioElement, offsetX, progressBarWidth, isRestricted = false, maxDuration = 60) {
    seekToPositionWithLimit(audioElement, offsetX, progressBarWidth, { isRestricted, maxDuration });
}

// Setup progress bar drag functionality for card player
export function setupCardProgressBarDrag(progressBarContainer, audioElement, cardId, dotNetRef, isRestricted = false, maxDuration = 60) {
    setupProgressDrag(progressBarContainer, audioElement, { isRestricted, maxDuration });
}

// Setup volume bar drag functionality for card player
export function setupCardVolumeBarDrag(volumeBarContainer, audioElement, cardId, dotNetRef) {
    setupVolumeDrag(volumeBarContainer, audioElement, (volume, muted) => dotNetRef.invokeMethodAsync('UpdateCardVolume', cardId, volume, muted));
}

export function cleanupCardPlayer(cardId) {
    cardPlayers.delete(cardId);
}

export { changeTrack };
