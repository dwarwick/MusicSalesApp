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

## Authentication Flow

Authentication now uses ASP.NET Core Identity's SignInManager directly on the server side:
- No JavaScript interop required
- All authentication happens server-side
- Proper session management via Identity cookies
- Works correctly during server-side prerendering

## Troubleshooting

### If login doesn't work:
1. Verify database connection in appsettings.Development.json
2. Ensure migrations have been applied (automatic on startup)
3. Check application logs for authentication errors
4. Verify user exists in database with correct credentials
5. Try logging in with default users (admin@app.com or user@app.com / Password_123)

### If logout doesn't work:
1. Check application logs for any errors during logout
2. Verify the page reloads to /login after clicking Logout
3. Check that the `.AspNetCore.Identity.Application` cookie is removed from browser
4. If issues persist, try clearing all browser cookies and cache
