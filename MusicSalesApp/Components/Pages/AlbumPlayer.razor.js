import {
    initAudioPlayer as initSharedAudio,
    play as playAudio,
    pause as pauseAudio,
    seekTo as seekToTime,
    seekToPosition as seekToPositionWithLimit,
    getElementWidth,
    getDuration,
    setTrackSource,
    changeTrack,
    setupProgressBarDrag as setupProgressDrag,
    setupVolumeBarDrag as setupVolumeDrag,
    setVolume,
    getVolume,
    setMuted,
    isMuted
} from '../Shared/audioPlayer.js';

export function initAudioPlayer(audioElement, dotNetRef, isRestricted = false, maxDuration = 60) {
    initSharedAudio(audioElement, {
        onTimeUpdate: (time) => dotNetRef.invokeMethodAsync('UpdateTime', time),
        onDuration: (duration) => dotNetRef.invokeMethodAsync('UpdateDuration', duration),
        onEnded: () => dotNetRef.invokeMethodAsync('AudioEnded')
    }, {
        isRestricted,
        maxDuration,
        onRestrictionReached: () => dotNetRef.invokeMethodAsync('AudioEnded')
    });
}

export function play(audioElement) {
    playAudio(audioElement);
}

export function pause(audioElement) {
    pauseAudio(audioElement);
}

export function seekTo(audioElement, time) {
    seekToTime(audioElement, time);
}

export function seekToPosition(audioElement, offsetX, progressBarWidth, isRestricted = false, maxDuration = 60) {
    seekToPositionWithLimit(audioElement, offsetX, progressBarWidth, { isRestricted, maxDuration });
}

export { getElementWidth, getDuration, setTrackSource, changeTrack, setVolume, getVolume, setMuted, isMuted };

export function setupProgressBarDrag(progressBarContainer, audioElement, dotNetRef, isRestricted = false, maxDuration = 60) {
    setupProgressDrag(progressBarContainer, audioElement, { isRestricted, maxDuration });
}

export function setupVolumeBarDrag(volumeBarContainer, audioElement, dotNetRef) {
    setupVolumeDrag(volumeBarContainer, audioElement, (volume, muted) => dotNetRef.invokeMethodAsync('UpdateVolume', volume, muted));
}
