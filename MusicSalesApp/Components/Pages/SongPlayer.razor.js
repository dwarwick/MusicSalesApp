export function initAudioPlayer(audioElement, dotNetRef) {
    if (!audioElement) return;

    audioElement.addEventListener('timeupdate', () => {
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

export function seekToPosition(audioElement, offsetX, progressBarWidth) {
    if (audioElement && progressBarWidth > 0) {
        const percentage = offsetX / progressBarWidth;
        const newTime = audioElement.duration * percentage;
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
