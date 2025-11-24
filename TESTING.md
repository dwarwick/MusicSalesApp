# Testing Authentication

## Setup
1. Copy `MusicSalesApp/appsettings.Development.json.sample` to `MusicSalesApp/appsettings.Development.json`
2. Run the application: `dotnet run --project MusicSalesApp`
3. Open browser to `https://localhost:7173` (or the port shown in console)

## Test Login Flow

### Test 1: Admin User Login
1. Navigate to the home page
2. **Verify**: NavMenu shows "Login" link (not "Logout")
3. **Verify**: No authentication cookies in browser dev tools under Application > Cookies
4. Click "Login" link
5. Enter credentials:
   - Username: `admin@app.com`
   - Password: `Password_123`
6. Click "Login" button
7. **Expected Result**:
   - Page reloads and navigates to home page
   - NavMenu now shows "Logout" link (not "Login")
   - Authentication cookie appears in browser dev tools:
     - Name: `.AspNetCore.Identity.Application`
     - Path: `/`
     - HttpOnly: Yes
     - Secure: Yes (on HTTPS)
   - User is authenticated

### Test 2: Regular User Login
1. If logged in, click "Logout" first
2. Click "Login" link
3. Enter credentials:
   - Username: `user@app.com`
   - Password: `Password_123`
4. Click "Login" button
5. **Expected Result**: Same as Test 1

### Test 3: Invalid Credentials
1. If logged in, click "Logout" first
2. Click "Login" link
3. Enter invalid credentials:
   - Username: `admin@app.com`
   - Password: `WrongPassword`
4. Click "Login" button
5. **Expected Result**:
   - Error message displayed: "Invalid username or password."
   - No page reload
   - NavMenu still shows "Login"
   - No authentication cookie set

### Test 4: Logout
1. Login with valid credentials
2. **Verify**: NavMenu shows "Logout"
3. Click "Logout" link
4. **Expected Result**:
   - Page reloads
   - NavMenu now shows "Login"
   - Authentication cookie is removed from browser

## Cookie Details
The authentication cookie should be named `.AspNetCore.Identity.Application` (not `.AspNetCore.Cookies` or any other name).

## Troubleshooting

### If login doesn't work:
1. Check browser console for JavaScript errors
2. Check network tab for API call to `/api/auth/login`
3. Verify response is 200 OK
4. Check that cookie is set in response headers
5. Verify database connection in appsettings.Development.json
6. Ensure migrations have been applied (automatic on startup)

### If logout doesn't work:
1. Check browser console for "Logout successful" message
2. Check network tab for API call to `/api/auth/logout`
3. Verify response is 200 OK
4. Check that Set-Cookie header removes the authentication cookie
5. After clicking logout, the page should reload and the cookie should be gone
6. If cookie persists, clear browser cache/cookies and try again
