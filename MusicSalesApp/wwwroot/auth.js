window.loginUser = async function(username, password) {
    try {
        const response = await fetch('/api/auth/login', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ username, password }),
            credentials: 'same-origin' // Ensure cookies are sent and received
        });

        return response.ok;
    } catch (error) {
        console.error('Login error:', error);
        return false;
    }
};

window.logoutUser = async function() {
    try {
        const response = await fetch('/api/auth/logout', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            credentials: 'same-origin' // Ensure cookies are sent and received
        });
        
        // Wait for the response to ensure logout completed
        if (response.ok) {
            console.log('Logout successful');
        } else {
            console.warn('Logout response not OK:', response.status);
        }
    } catch (error) {
        console.error('Logout error:', error);
    }
};
