// Click-and-drag binding for the progress and volume bars.
//
// One implementation for all three players. They had a copy each, identical apart from what the
// drag does, and each copy carried the same two faults - so fixing them in one place is also what
// stops the next copy inheriting them.

/**
 * The live registration per bar element, so re-binding can replace rather than accumulate.
 * Weak, so a bar belonging to a card that has gone away does not pin the element in memory.
 */
const bindings = new WeakMap();

/**
 * Binds mouse and touch dragging on a bar, reporting the pointer's X position as it moves.
 *
 * @param container the bar element
 * @param onDrag called with a clientX whenever the pointer is pressed or dragged
 */
export function bindBarDrag(container, onDrag) {
    if (!container || typeof onDrag !== 'function') {
        return;
    }

    // Binding the same bar again REPLACES its previous registration.
    //
    // Every player re-runs its drag setup each time a track starts, and four of the six listeners
    // below live on `document`. Without this they accumulated for the lifetime of the page: after
    // twenty tracks, every mouse movement anywhere ran eighty handlers, each measuring a bar that
    // belonged to a song which had long finished - and each closure kept that song's audio element
    // alive along with it.
    bindings.get(container)?.abort();

    const controller = new AbortController();
    const signal = controller.signal;
    bindings.set(container, controller);

    let isDragging = false;
    const stopDragging = () => { isDragging = false; };

    container.addEventListener('mousedown', (event) => {
        isDragging = true;
        onDrag(event.clientX);
        event.preventDefault();
    }, { signal });

    document.addEventListener('mousemove', (event) => {
        if (isDragging) {
            onDrag(event.clientX);
        }
    }, { signal });

    document.addEventListener('mouseup', stopDragging, { signal });

    // passive: false is a declaration here, not a change in behaviour - this handler really does
    // cancel the gesture, because dragging a volume or progress bar must not scroll the page
    // underneath it. Saying so is what stops the browser having to assume it MIGHT: with the flag
    // absent, Chrome cannot know whether the handler will call preventDefault, so it waits for this
    // handler before it can begin scrolling anywhere on the element, and logs the "non-passive event
    // listener to a scroll-blocking touchstart" violation to say that it had to.
    container.addEventListener('touchstart', (event) => {
        isDragging = true;
        if (event.touches.length > 0) {
            onDrag(event.touches[0].clientX);
        }
        event.preventDefault();
    }, { passive: false, signal });

    // Passive, unlike the touchstart above, because these genuinely never cancel anything. The
    // touchstart has already taken the gesture, so there is no scrolling left for them to block and
    // the browser is free to stop consulting them.
    document.addEventListener('touchmove', (event) => {
        if (isDragging && event.touches.length > 0) {
            onDrag(event.touches[0].clientX);
        }
    }, { passive: true, signal });

    document.addEventListener('touchend', stopDragging, { passive: true, signal });
}

/**
 * Releases a bar's listeners.
 *
 * Rebinding does this on its own, so this is only needed when a player is torn down without a
 * replacement taking its place.
 */
export function unbindBarDrag(container) {
    if (!container) {
        return;
    }

    bindings.get(container)?.abort();
    bindings.delete(container);
}
