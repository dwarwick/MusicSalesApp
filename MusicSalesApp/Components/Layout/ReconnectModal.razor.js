// Set up event handlers for Blazor reconnection events
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

// Auto-retry configuration
let scheduledRetryAttempts = 0; // Tracks how many retry attempts have been scheduled
const MAX_RETRY_ATTEMPTS = 3;
// Retry delays with exponential-like backoff (2s, 5s, 10s)
const RETRY_DELAYS = [2000, 5000, 10000];
let autoRetryTimeout = null;

// Connection health monitoring (optional logging - development only)
let lastActivityTime = Date.now();
let connectionHealthInterval = null;
let isDevelopment = false;

// Detect if we're in development mode
// Check for common development indicators
try {
    isDevelopment = window.location.hostname === 'localhost' || 
                   window.location.hostname === '127.0.0.1' ||
                   window.location.hostname.includes('local') ||
                   document.documentElement.hasAttribute('data-development');
} catch (e) {
    // If detection fails, assume production (safer default)
    isDevelopment = false;
}

// Start monitoring connection health (development only)
function startConnectionHealthMonitor() {
    if (!isDevelopment) return; // Only run in development
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

// Track user interactions and Blazor activity
function setupActivityTracking() {
    // Update activity on any user interaction
    const interactionEvents = ['click', 'keydown', 'touchstart', 'scroll'];
    interactionEvents.forEach(eventType => {
        document.addEventListener(eventType, updateLastActivity, { passive: true });
    });
    
    // Track Blazor navigation events
    if (typeof Blazor !== 'undefined') {
        // Blazor fires events on the document when circuit state changes
        document.addEventListener('DOMContentLoaded', updateLastActivity);
        
        // Monitor for Blazor-specific events
        const originalAddEventListener = reconnectModal.addEventListener;
        reconnectModal.addEventListener = function(type, listener, options) {
            if (type === 'components-reconnect-state-changed') {
                // Wrap the listener to update activity
                const wrappedListener = function(event) {
                    updateLastActivity();
                    return listener.call(this, event);
                };
                return originalAddEventListener.call(this, type, wrappedListener, options);
            }
            return originalAddEventListener.call(this, type, listener, options);
        };
    }
    
    // Track page visibility changes - reset activity when page becomes visible
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') {
            updateLastActivity();
        }
    });
}

// Monitor Blazor connection events to track activity
if (typeof Blazor !== 'undefined') {
    // Start monitoring on page load (development only)
    startConnectionHealthMonitor();
    if (isDevelopment) {
        console.log('[SignalR Health] Connection health monitoring started. Logs will appear every 30 seconds.');
    }
    
    // Set up activity tracking
    setupActivityTracking();
}

function handleReconnectStateChanged(event) {
    updateLastActivity(); // Track reconnection events as activity
    
    if (event.detail.state === "show") {
        // Blazor has detected connection loss and is attempting to reconnect automatically
        // We don't need to do anything here - just let Blazor handle initial reconnection attempts
        if (isDevelopment) {
            console.log("Connection lost. Blazor is attempting to reconnect...");
        }
    } else if (event.detail.state === "hide") {
        // Connection restored successfully, cancel any pending retries
        cancelAutoRetry();
        scheduledRetryAttempts = 0;
        reconnectModal.close();
        if (isDevelopment) {
            console.log("Connection restored successfully.");
        }
    } else if (event.detail.state === "failed") {
        // All of Blazor's automatic reconnection attempts have failed
        // Now we take over with our own retry logic
        if (isDevelopment) {
            console.log("Blazor reconnection failed. Starting custom retry logic...");
        }
        scheduledRetryAttempts = 0; // Reset counter at start of our retry sequence
        scheduleAutoRetry();
    } else if (event.detail.state === "rejected") {
        // Server rejected the connection, reload immediately
        if (isDevelopment) {
            console.log("Connection rejected by server. Reloading page...");
        }
        location.reload();
    }
}

function scheduleAutoRetry() {
    // Clear any existing retry timeout
    cancelAutoRetry();
    
    // If we've exceeded max attempts, reload the page
    if (scheduledRetryAttempts >= MAX_RETRY_ATTEMPTS) {
        if (isDevelopment) {
            console.log("Max retry attempts reached. Reloading page...");
        }
        location.reload();
        return;
    }
    
    // Get delay for current attempt (use last delay if we exceed array length)
    const delay = scheduledRetryAttempts < RETRY_DELAYS.length 
        ? RETRY_DELAYS[scheduledRetryAttempts] 
        : RETRY_DELAYS[RETRY_DELAYS.length - 1];
    
    // Increment counter before scheduling
    scheduledRetryAttempts++;
    if (isDevelopment) {
        console.log(`Scheduling retry attempt ${scheduledRetryAttempts} of ${MAX_RETRY_ATTEMPTS} in ${delay}ms...`);
    }
    
    // Schedule the retry with error handling
    autoRetryTimeout = setTimeout(async () => {
        try {
            await retry();
        } catch (err) {
            // Always log errors, even in production (they're important)
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
                if (isDevelopment) {
                    console.log("Circuit resume failed. Reloading page...");
                }
                location.reload();
            } else {
                // Resume successful, reset retry counter
                if (isDevelopment) {
                    console.log("Circuit resumed successfully.");
                }
                scheduledRetryAttempts = 0;
                cancelAutoRetry();
                reconnectModal.close();
            }
        } else {
            // Reconnect successful, reset retry counter
            if (isDevelopment) {
                console.log("Reconnection successful.");
            }
            scheduledRetryAttempts = 0;
            cancelAutoRetry();
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        // Schedule another retry
        // Always log errors, even in production
        console.error("Retry failed:", err);
        scheduleAutoRetry();
    }
}
