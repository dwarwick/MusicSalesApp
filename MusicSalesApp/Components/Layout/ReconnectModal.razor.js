// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

// Auto-retry configuration
let retryAttempts = 0;
const MAX_RETRY_ATTEMPTS = 3;
const RETRY_DELAYS = [2000, 5000, 10000]; // Exponential backoff in milliseconds
let autoRetryTimeout = null;

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        // Don't show the modal, start automatic retry process instead
        retryAttempts = 0;
        scheduleAutoRetry();
    } else if (event.detail.state === "hide") {
        // Connection restored, cancel any pending retries
        cancelAutoRetry();
        retryAttempts = 0;
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        // Connection failed, but keep retrying automatically
        scheduleAutoRetry();
    } else if (event.detail.state === "rejected") {
        // Server rejected the connection, reload immediately
        location.reload();
    }
}

function scheduleAutoRetry() {
    // Clear any existing retry timeout
    cancelAutoRetry();
    
    // If we've exceeded max attempts, reload the page
    if (retryAttempts >= MAX_RETRY_ATTEMPTS) {
        console.log("Max retry attempts reached. Reloading page...");
        location.reload();
        return;
    }
    
    // Get delay for current attempt (use last delay if we exceed array length)
    const delay = RETRY_DELAYS[Math.min(retryAttempts, RETRY_DELAYS.length - 1)];
    
    console.log(`Scheduling auto-retry attempt ${retryAttempts + 1} in ${delay}ms...`);
    
    // Schedule the retry
    autoRetryTimeout = setTimeout(async () => {
        retryAttempts++;
        await retry();
    }, delay);
}

function cancelAutoRetry() {
    if (autoRetryTimeout) {
        clearTimeout(autoRetryTimeout);
        autoRetryTimeout = null;
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // Try to resume the circuit
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                // Resume failed, reload the page
                location.reload();
            } else {
                // Resume successful, reset retry counter
                retryAttempts = 0;
                cancelAutoRetry();
                reconnectModal.close();
            }
        } else {
            // Reconnect successful, reset retry counter
            retryAttempts = 0;
            cancelAutoRetry();
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        // Schedule another retry
        console.error("Retry failed:", err);
        scheduleAutoRetry();
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        location.reload();
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
