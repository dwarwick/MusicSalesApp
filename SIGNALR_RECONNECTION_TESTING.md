# Testing SignalR Reconnection Locally in Visual Studio

This guide explains how to test the SignalR reconnection improvements before deploying to production.

## ⚠️ Important: Understanding SignalR Keep-Alive

**SignalR keep-alive pings are NOT visible as console.log messages!**

The keep-alive mechanism works at the WebSocket protocol level. You won't see individual ping/pong log messages in the browser console. Instead:

1. **Ping/pong frames** are visible in DevTools → Network tab → WS → Messages/Frames
2. **Connection health monitor** (added in latest update) logs every 30 seconds to confirm the connection is alive
3. **No disconnection** means it's working - if you don't see "Connection lost" messages, the keep-alive is doing its job

See Test 2 below for detailed verification steps.

## Prerequisites

- Visual Studio 2022 (or Visual Studio Code with C# extension)
- .NET 10.0 SDK installed
- Chrome/Edge browser with Developer Tools

## Running the Application

### Option 1: Visual Studio 2022

1. **Open the Solution**
   - Open `MusicSalesApp.slnx` in Visual Studio 2022

2. **Set Startup Project**
   - Right-click on `MusicSalesApp` project in Solution Explorer
   - Select "Set as Startup Project"

3. **Choose Launch Profile**
   - Click the dropdown next to the green play button
   - Select "https" profile (recommended) or "http"
   - This will launch on `https://localhost:7173` or `http://localhost:5162`

4. **Start Debugging**
   - Press F5 or click the green play button
   - Browser should open automatically to the application

### Option 2: Visual Studio Code or Command Line

```bash
cd MusicSalesApp
dotnet run --launch-profile https
```

The application will be available at:
- HTTPS: https://localhost:7173
- HTTP: http://localhost:5162

## Testing Reconnection Scenarios

### Test 1: Verify Modal is Hidden

1. **Open Browser Developer Tools** (F12)
2. **Navigate to Elements/Inspector tab**
3. **Search for** `components-reconnect-modal`
4. **Verify:** The modal should have `display: none !important` in its styles
5. **Verify:** The modal should have `data-nosnippet` attribute

✅ **Expected Result:** Modal is present in DOM but completely hidden

### Test 2: Verify Keep-Alive is Working

**Important:** SignalR keep-alive pings happen at the WebSocket protocol level and are NOT visible as console.log messages by default. Here's how to verify they're working:

**Method 1: Check WebSocket Frames (Most Accurate)**
1. **Open Developer Tools** (F12)
2. **Go to Network tab**
3. **Filter by WS (WebSocket)** - click the "WS" button in the filter bar
4. **Refresh the page** to see new connections
5. **Click on a websocket connection** (usually named like `blazor?id=...`)
6. **Go to the "Messages" or "Frames" tab**
7. **Watch for ping/pong frames:**
   - You should see Type: "ping" or "pong" frames every 15 seconds
   - In Chrome/Edge: Green arrows indicate ping/pong
   - In Firefox: Look for "Ping" and "Pong" frame types

✅ **Expected Result:** Ping/pong frames appear every 15 seconds in WebSocket messages

**Method 2: Connection Health Monitor (New)**
With the latest update, the page now logs connection health to the console:

1. **Open Browser Console** (F12)
2. **Look for health check messages every 30 seconds:**
   ```
   [SignalR Health] Connection active. Last activity: 15s ago
   [SignalR Health] Connection active. Last activity: 30s ago
   ```
3. **These messages confirm** the connection is alive even if you don't see individual pings

✅ **Expected Result:** Health check logs appear every 30 seconds showing connection is active

**Method 3: No Disconnection Messages**
The simplest test - if the connection is working:
- You should NOT see "Connection lost" messages
- You should NOT see "Blazor is attempting to reconnect" messages
- The page should work normally for extended periods

✅ **Expected Result:** No disconnection messages after sitting idle for 5+ minutes

### Test 3: Simulate Connection Loss (Server Stop)

This tests the automatic reconnection when the server goes down.

1. **Open the application** in browser
2. **Open Browser Console** (F12)
3. **Stop the server** in Visual Studio:
   - Click the red "Stop Debugging" square button
   - Or press Shift+F5
4. **Watch the Console logs:**
   - "Connection lost. Blazor is attempting to reconnect..."
   - "Blazor reconnection failed. Starting custom retry logic..."
   - "Scheduling retry attempt 1 of 3 in 2000ms..."
   - "Scheduling retry attempt 2 of 3 in 5000ms..."
   - "Scheduling retry attempt 3 of 3 in 10000ms..."
5. **Observe behavior:**
   - NO modal should appear to the user
   - After 3 attempts (17 seconds total), page should auto-reload
6. **Restart the server** before the page reloads to test reconnection success

✅ **Expected Result:** Silent reconnection attempts, no UI shown, page reloads if all fail

### Test 4: Simulate Connection Loss (Network Throttling)

This simulates a temporary network issue.

1. **Open Browser DevTools** (F12)
2. **Go to Network tab**
3. **Enable Network Throttling:**
   - Chrome: Click "Online" dropdown → Select "Offline"
   - Edge: Same as Chrome
   - Firefox: Click throttling icon → Select "Offline"
4. **Wait 5-10 seconds**
5. **Re-enable Network:**
   - Set back to "Online"
6. **Watch Console logs:**
   - Should see reconnection attempts
   - Should eventually reconnect successfully
   - Log: "Connection restored successfully."

✅ **Expected Result:** Automatic reconnection when network returns, no modal shown

### Test 5: Long-Running Session Test

Tests that connections stay alive during normal usage.

1. **Start the application**
2. **Navigate through different pages**
3. **Leave browser open for 5+ minutes**
4. **Interact with the application** (play music, add to cart, etc.)
5. **Check Console for errors**

✅ **Expected Result:** No disconnections, no reconnection attempts, smooth operation

### Test 6: Verify SEO Crawler Won't See Modal Text

1. **Open application** in browser
2. **Right-click page** → "View Page Source" (or Ctrl+U)
3. **Search for** "Rejoining the server"
4. **Verify:** Text should be in a tag with `data-nosnippet` attribute
5. **Verify:** Modal has `display: none` in styles

✅ **Expected Result:** Modal text won't be indexed by search engines

## Expected Console Logs

### Normal Operation (Keep-Alive Working)

**New: Connection Health Monitor**
```
[SignalR Health] Connection health monitoring started. Logs will appear every 30 seconds.
[SignalR Health] Connection active. Last activity: 0s ago
[SignalR Health] Connection active. Last activity: 15s ago
[SignalR Health] Connection active. Last activity: 30s ago
```

**Note:** Individual ping/pong messages are NOT logged to console. They happen at the WebSocket protocol level and can only be seen in the Network tab → WebSocket → Messages/Frames.

If you see the health monitor reporting "Connection active" every 30 seconds, your keep-alive is working correctly.

### Connection Lost → Blazor Retrying
```
Connection lost. Blazor is attempting to reconnect...
```

### Blazor Failed → Custom Retry Logic
```
Blazor reconnection failed. Starting custom retry logic...
Scheduling retry attempt 1 of 3 in 2000ms...
Retry failed: [Error details]
Scheduling retry attempt 2 of 3 in 5000ms...
Retry failed: [Error details]
Scheduling retry attempt 3 of 3 in 10000ms...
Max retry attempts reached. Reloading page...
```

### Successful Reconnection
```
Reconnection successful.
Connection restored successfully.
```

### Successful Circuit Resume
```
Circuit resumed successfully.
Connection restored successfully.
```

## Troubleshooting

### Modal Still Appears
- **Check:** Browser cache - try Ctrl+Shift+R to hard refresh
- **Check:** Make sure you're running the latest code
- **Verify:** `ReconnectModal.razor.css` has `display: none !important`

### Connection Keeps Dropping
- **Check:** SQL Server LocalDB is running
- **Check:** Connection string in `appsettings.Development.json`
- **Check:** No firewall blocking localhost connections

### No Console Logs Appearing
- **Enable verbose logging** in DevTools Console settings
- **Check:** Console filter isn't hiding messages
- **Verify:** Application is running in Development mode

## Advanced Testing with Browser DevTools

### Simulate Slow Network
1. DevTools → Network tab
2. Set throttling to "Slow 3G" or "Fast 3G"
3. Verify connection stays alive with keep-alive pings

### Monitor WebSocket Connection
1. DevTools → Network tab → WS (WebSocket) filter
2. Click on the SignalR connection
3. Go to "Messages" tab
4. Watch ping/pong messages every 15 seconds

### Check Network Timing
1. DevTools → Network tab
2. Look for requests to `/streamcounthub` or similar SignalR endpoints
3. Verify connection establishment timing (should be < 15s handshake timeout)

## Configuration Reference

Current SignalR settings (from `Program.cs`):

```csharp
// Keep-alive interval - 15 seconds
options.KeepAliveInterval = TimeSpan.FromSeconds(15);

// Client timeout - 60 seconds
options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);

// Handshake timeout - 15 seconds
options.HandshakeTimeout = TimeSpan.FromSeconds(15);

// Circuit retention - 3 minutes
options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
```

Retry delays: **2s, 5s, 10s** (total ~17 seconds before page reload)

## What Changed

### Before
- Modal appeared with "Rejoining the server..." text
- Text was visible in Google search results
- User had to click "Retry" button
- No keep-alive configuration

### After
- Modal is completely hidden (`display: none !important`)
- Automatic keep-alive with 15s pings
- Silent reconnection with exponential backoff
- Auto-reload after 3 failed attempts
- No user interaction required
- SEO-friendly (no modal text in search results)

## Questions or Issues?

If you encounter any issues during testing:
1. Check the console logs for error details
2. Verify all configuration settings are correct
3. Ensure SQL Server LocalDB is running
4. Try a hard refresh (Ctrl+Shift+R) to clear cache
