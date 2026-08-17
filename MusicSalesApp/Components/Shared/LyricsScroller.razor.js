/*
 * Karaoke highlighting and scrolling.
 *
 * WHY THIS IS HERE AND NOT IN C#
 * ------------------------------
 * Every player in this application already reports its position to .NET through the audio element's
 * `timeupdate` event. That is the obvious source for a highlighter and it cannot be used for one:
 * `timeupdate` fires roughly four times a second, and this is Blazor SERVER, so each tick is a
 * SignalR round trip ending in StateHasChanged over the whole page. Highlighting a word four times a
 * second, late by a network hop, on a page that might have a dozen playing cards, is both visibly
 * wrong and expensive.
 *
 * So the words are rendered once by Blazor with data-l/data-w attributes, and everything that has to
 * happen per frame happens here, against the audio element's own clock. Nothing in this file calls
 * back into .NET except a word click, which happens at human speed.
 *
 * ONE MAP, NOT MODULE-LEVEL STATE
 * -------------------------------
 * The library grid renders one scroller per card and Blazor loads this module once for all of them,
 * so every instance's state is keyed by an id minted in C#.
 */

const instances = new Map();

/** Below this the line is considered already centred; avoids a transform on every frame. */
const RECENTRE_EPSILON_PX = 1;

export function init(id, root, audio, dotNetRef, interactive) {
    // Checked by capability, not by truthiness. An ElementReference that was never assigned - which
    // happens whenever the audio element is rendered after this component in its parent's markup -
    // arrives here as an object rather than as null, so `!audio` does not catch it. Calling
    // addEventListener on it throws, and an unhandled JSException on Blazor Server does not fail
    // quietly: it tears down the circuit and the whole page reports "An unhandled error has occurred".
    if (!root || !audio || typeof audio.addEventListener !== 'function') {
        return;
    }

    dispose(id);

    const state = {
        root,
        audio,
        dotNetRef,
        track: root.querySelector('.lyrics-scroller-track'),
        words: [],
        lines: [],
        starts: [],
        ends: [],
        lineOf: [],
        wordOf: [],
        activeIndex: -1,
        activeLine: -1,
        frame: 0,
        listeners: [],
    };

    const on = (target, event, handler) => {
        target.addEventListener(event, handler);
        state.listeners.push([target, event, handler]);
    };

    // The loop runs only while the audio is actually playing. An idle 60fps loop per card is pure
    // waste on a page showing a grid of them.
    on(audio, 'play', () => start(state));
    on(audio, 'playing', () => start(state));
    on(audio, 'pause', () => { stop(state); update(state); });
    on(audio, 'ended', () => { stop(state); update(state); });

    // Seeking has to update even while paused - a creator scrubbing to find a line expects the
    // highlight to follow, and the rAF loop is not running to do it for them.
    on(audio, 'seeked', () => update(state, true));
    on(audio, 'seeking', () => update(state, true));
    on(audio, 'loadedmetadata', () => update(state, true));

    if (interactive) {
        // ONE delegated listener, not one per word. A four-minute song is a few thousand words.
        on(root, 'click', (event) => {
            const target = event.target.closest('[data-w]');
            if (!target || !state.dotNetRef) {
                return;
            }

            const line = Number(target.getAttribute('data-l'));
            const word = Number(target.getAttribute('data-w'));

            if (Number.isInteger(line) && Number.isInteger(word)) {
                state.dotNetRef.invokeMethodAsync('WordClicked', line, word);
            }
        });
    }

    instances.set(id, state);
    cacheElements(state);
}

/** Hand over timings the caller already had (the editor, and any draft). */
export function setTimings(id, timings) {
    const state = instances.get(id);
    if (!state || !timings) {
        return;
    }

    state.starts = timings.starts || [];
    state.ends = timings.ends || [];
    state.lineOf = timings.lineOf || [];
    state.wordOf = timings.wordOf || [];
    state.activeIndex = -1;
    state.activeLine = -1;

    cacheElements(state);
    update(state, true);

    if (!state.audio.paused) {
        start(state);
    }
}

/** Fetch published timings over the public media route. */
export async function loadTimings(id, url) {
    const state = instances.get(id);
    if (!state || !url) {
        return;
    }

    try {
        const response = await fetch(url, { credentials: 'same-origin' });
        if (!response.ok) {
            // A 404 here is the normal answer for timings the creator has not published, so this is
            // not an error condition - the scroller simply has nothing to show.
            return;
        }

        const document = await response.json();
        setTimings(id, flatten(document));
    } catch {
        // Network trouble costs the lyrics, not the page.
    }
}

/**
 * Flatten the document the same way the C# side does.
 *
 * Only used on the fetch path: the published JSON is the Python writer's nested shape, and the
 * per-frame lookup wants one ascending array rather than a tree to descend.
 */
function flatten(document) {
    const starts = [];
    const ends = [];
    const lineOf = [];
    const wordOf = [];

    (document.lines || []).forEach((line, lineIndex) => {
        if (line.startMs === null || line.startMs === undefined) {
            return;
        }

        const words = line.words || [];

        if (words.length === 0) {
            starts.push(line.startMs);
            ends.push(line.endMs);
            lineOf.push(lineIndex);
            wordOf.push(0);
            return;
        }

        words.forEach((word, wordIndex) => {
            if (word.startMs === null || word.startMs === undefined) {
                return;
            }
            starts.push(word.startMs);
            ends.push(word.endMs);
            lineOf.push(lineIndex);
            wordOf.push(wordIndex);
        });
    });

    return { starts, ends, lineOf, wordOf, durationMs: document.durationMs };
}

/**
 * Re-read the DOM after Blazor has rendered a different document.
 *
 * Cached because querySelectorAll on every frame, for every playing card, is exactly the kind of
 * cost that makes a page feel heavy for no visible reason.
 */
function cacheElements(state) {
    state.words = Array.from(state.root.querySelectorAll('[data-w]'));
    state.lines = Array.from(state.root.querySelectorAll('.lyrics-line[data-l]'));
    state.track = state.root.querySelector('.lyrics-scroller-track');

    state.wordByKey = new Map();
    state.words.forEach((element) => {
        state.wordByKey.set(`${element.getAttribute('data-l')}:${element.getAttribute('data-w')}`, element);
    });

    state.lineByIndex = new Map();
    state.lines.forEach((element) => {
        state.lineByIndex.set(Number(element.getAttribute('data-l')), element);
    });
}

function start(state) {
    if (state.frame) {
        return;
    }

    const tick = () => {
        update(state);
        state.frame = requestAnimationFrame(tick);
    };

    state.frame = requestAnimationFrame(tick);
}

function stop(state) {
    if (state.frame) {
        cancelAnimationFrame(state.frame);
        state.frame = 0;
    }
}

/**
 * Move the highlight to wherever the audio actually is.
 *
 * The position is re-derived from currentTime every frame rather than accumulated locally, so
 * buffering, a seek, or a tab that was backgrounded all correct themselves on the next frame instead
 * of drifting permanently.
 */
function update(state, force) {
    const count = state.starts.length;
    if (count === 0) {
        return;
    }

    const ms = state.audio.currentTime * 1000;
    let index = state.activeIndex;

    if (force || index < 0 || index >= count || ms < state.starts[index]) {
        index = search(state.starts, ms);
    } else {
        // The common case: time moved forward a frame's worth, so the answer is here or just after.
        while (index + 1 < count && state.starts[index + 1] <= ms) {
            index++;
        }
    }

    if (index === state.activeIndex && !force) {
        return;
    }

    state.activeIndex = index;

    const previous = state.root.querySelector('.lyrics-word--active');
    if (previous) {
        previous.classList.remove('lyrics-word--active');
    }

    if (index < 0) {
        return;
    }

    const element = state.wordByKey.get(`${state.lineOf[index]}:${state.wordOf[index]}`);
    if (element) {
        element.classList.add('lyrics-word--active');
    }

    const line = state.lineOf[index];
    if (line !== state.activeLine) {
        state.activeLine = line;
        highlightLine(state, line);
        centre(state, line);
    }
}

/** The last index whose start is at or before `ms`, or -1 before the first word. */
function search(starts, ms) {
    let low = 0;
    let high = starts.length - 1;
    let found = -1;

    while (low <= high) {
        const mid = (low + high) >> 1;
        if (starts[mid] <= ms) {
            found = mid;
            low = mid + 1;
        } else {
            high = mid - 1;
        }
    }

    return found;
}

function highlightLine(state, lineIndex) {
    const previous = state.root.querySelector('.lyrics-line--active');
    if (previous) {
        previous.classList.remove('lyrics-line--active');
    }

    const element = state.lineByIndex.get(lineIndex);
    if (element) {
        element.classList.add('lyrics-line--active');
    }
}

/**
 * Bring the current line to the middle by TRANSLATING THE TRACK, never by scrolling.
 *
 * scrollIntoView({ block: 'center' }) would be one line of code and would scroll every scrollable
 * ancestor to get there - yanking the library grid, or the player page, or a dialog, whenever a song
 * moved to the next line. Moving a child inside a fixed, clipped box cannot affect anything outside
 * it. The easing is a CSS transition on the track, so this stays one style write per line change.
 */
function centre(state, lineIndex) {
    const element = state.lineByIndex.get(lineIndex);
    if (!element || !state.track) {
        return;
    }

    const target = element.offsetTop + (element.offsetHeight / 2) - (state.root.clientHeight / 2);
    const next = -Math.max(0, target);
    const current = state.currentOffset ?? 0;

    if (Math.abs(next - current) < RECENTRE_EPSILON_PX) {
        return;
    }

    state.currentOffset = next;
    state.track.style.transform = `translate3d(0, ${next}px, 0)`;
}

/** Mark a word as selected in the editor. Purely visual; selection itself lives in C#. */
export function setSelectedWord(id, lineIndex, wordIndex) {
    const state = instances.get(id);
    if (!state) {
        return;
    }

    const previous = state.root.querySelector('.lyrics-word--selected');
    if (previous) {
        previous.classList.remove('lyrics-word--selected');
    }

    const element = state.wordByKey.get(`${lineIndex}:${wordIndex}`);
    if (element) {
        element.classList.add('lyrics-word--selected');
    }
}

export function dispose(id) {
    const state = instances.get(id);
    if (!state) {
        return;
    }

    stop(state);

    state.listeners.forEach(([target, event, handler]) => {
        target.removeEventListener(event, handler);
    });

    state.listeners = [];
    state.words = [];
    state.lines = [];
    state.wordByKey = null;
    state.lineByIndex = null;

    instances.delete(id);
}
