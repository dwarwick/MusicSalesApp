// Set up event handlers for Blazor reconnection events
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

// Auto-retry configuration
let scheduledRetryAttempts = 0; // Tracks how many retry attempts have been scheduled
const MAX_RETRY_ATTEMPTS = 3;
// Retry delays with exponential-like backoff (2s, 5s, 10s)
const RETRY_DELAYS = [2000, 5000, 10000];
let autoRetryTimeout = null;

// Connection health monitoring (optional logging)
let lastActivityTime = Date.now();
let connectionHealthInterval = null;

// Start monitoring connection health
function startConnectionHealthMonitor() {
    if (connectionHealthInterval) return;
    
    connectionHealthInterval = setInterval(() => {
        const now = Date.now();
        const timeSinceLastActivity = now - lastActivityTime;
        
        // Log connection health every 30 seconds
        if (timeSinceLastActivity < 65000) { // 65 seconds (slightly more than client timeout)
            console.log(`[SignalR Health] Connection active. Last activity: ${Math.floor(timeSinceLastActivity / 1000)}s ago`);
        } else {
            console.warn(`[SignalR Health] No activity for ${Math.floor(timeSinceLastActivity / 1000)}s. Connection may be stale.`);
        }
    }, 30000); // Check every 30 seconds
}

// Update last activity time
function updateLastActivity() {
    lastActivityTime = Date.now();
}

// Monitor Blazor connection events to track activity
if (typeof Blazor !== 'undefined') {
    // Start monitoring on page load
    startConnectionHealthMonitor();
    console.log('[SignalR Health] Connection health monitoring started. Logs will appear every 30 seconds.');
    
    // Try to hook into Blazor's event system to track actual activity
    // This is a best-effort approach as Blazor's internals may change
    const originalConsoleLog = console.log;
    console.log = function(...args) {
        const message = args.join(' ');
        if (message.includes('SignalR') || message.includes('blazor')) {
            updateLastActivity();
        }
        originalConsoleLog.apply(console, args);
    };
}

function handleReconnectStateChanged(event) {
    updateLastActivity(); // Track reconnection events as activity
    
    if (event.detail.state === "show") {
        // Blazor has detected connection loss and is attempting to reconnect automatically
        // We don't need to do anything here - just let Blazor handle initial reconnection attempts
        console.log("Connection lost. Blazor is attempting to reconnect...");
    } else if (event.detail.state === "hide") {
        // Connection restored successfully, cancel any pending retries
        cancelAutoRetry();
        scheduledRetryAttempts = 0;
        reconnectModal.close();
        console.log("Connection restored successfully.");
    } else if (event.detail.state === "failed") {
        // All of Blazor's automatic reconnection attempts have failed
        // Now we take over with our own retry logic
        console.log("Blazor reconnection failed. Starting custom retry logic...");
        scheduledRetryAttempts = 0; // Reset counter at start of our retry sequence
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
    if (scheduledRetryAttempts >= MAX_RETRY_ATTEMPTS) {
        console.log("Max retry attempts reached. Reloading page...");
        location.reload();
        return;
    }
    
    // Get delay for current attempt (use last delay if we exceed array length)
    const delay = scheduledRetryAttempts < RETRY_DELAYS.length 
        ? RETRY_DELAYS[scheduledRetryAttempts] 
        : RETRY_DELAYS[RETRY_DELAYS.length - 1];
    
    // Increment counter before scheduling
    scheduledRetryAttempts++;
    console.log(`Scheduling retry attempt ${scheduledRetryAttempts} of ${MAX_RETRY_ATTEMPTS} in ${delay}ms...`);
    
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
                scheduledRetryAttempts = 0;
                cancelAutoRetry();
                reconnectModal.close();
            }
        } else {
            // Reconnect successful, reset retry counter
            console.log("Reconnection successful.");
            scheduledRetryAttempts = 0;
            cancelAutoRetry();
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        // Schedule another retry
        console.error("Retry failed:", err);
        scheduleAutoRetry();
    }
}
