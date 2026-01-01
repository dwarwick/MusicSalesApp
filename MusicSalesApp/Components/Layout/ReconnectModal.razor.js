// Set up event handlers for Blazor reconnection events
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

// Auto-retry configuration
let retryAttempts = 0;
const MAX_RETRY_ATTEMPTS = 3;
// Retry delays with exponential-like backoff (2s, 5s, 10s)
const RETRY_DELAYS = [2000, 5000, 10000];
let autoRetryTimeout = null;

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        // Blazor has detected connection loss and is attempting to reconnect automatically
        // We don't need to do anything here - just let Blazor handle initial reconnection attempts
        console.log("Connection lost. Blazor is attempting to reconnect...");
    } else if (event.detail.state === "hide") {
        // Connection restored successfully, cancel any pending retries
        cancelAutoRetry();
        retryAttempts = 0;
        reconnectModal.close();
        console.log("Connection restored successfully.");
    } else if (event.detail.state === "failed") {
        // All of Blazor's automatic reconnection attempts have failed
        // Now we take over with our own retry logic
        console.log("Blazor reconnection failed. Starting custom retry logic...");
        retryAttempts = 0; // Reset counter at start of our retry sequence
        scheduleAutoRetry();
    } else if (event.detail.state === "rejected") {
        // Server rejected the connection, reload immediately
        console.log("Connection rejected by server. Reloading page...");
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
    const delay = retryAttempts < RETRY_DELAYS.length 
        ? RETRY_DELAYS[retryAttempts] 
        : RETRY_DELAYS[RETRY_DELAYS.length - 1];
    
    console.log(`Scheduling auto-retry attempt ${retryAttempts + 1} in ${delay}ms...`);
    
    // Increment counter now before the attempt
    retryAttempts++;
    
    // Schedule the retry with error handling
    autoRetryTimeout = setTimeout(async () => {
        try {
            await retry();
        } catch (err) {
            console.error("Unhandled error in retry:", err);
            // Schedule another retry (retry counter already incremented above)
            scheduleAutoRetry();
        }
    }, delay);
}

function cancelAutoRetry() {
    if (autoRetryTimeout) {
        clearTimeout(autoRetryTimeout);
        autoRetryTimeout = null;
    }
}

async function retry() {
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
                console.log("Circuit resume failed. Reloading page...");
                location.reload();
            } else {
                // Resume successful, reset retry counter
                console.log("Circuit resumed successfully.");
                retryAttempts = 0;
                cancelAutoRetry();
                reconnectModal.close();
            }
        } else {
            // Reconnect successful, reset retry counter
            console.log("Reconnection successful.");
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
