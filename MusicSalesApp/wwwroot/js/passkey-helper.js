window.passkeyHelper = {
    registerPasskey: async function (passkeyName, userId) {
        // Create an AbortController for timeout management
        const abortController = new AbortController();
        const signal = abortController.signal;
        
        // Set a 3-minute timeout for the entire operation
        // Cloud password managers like Google Password Manager may need extra time
        const timeoutId = setTimeout(() => {
            abortController.abort();
        }, 180000); // 180 seconds (3 minutes)
        
        try {
            // Begin registration - get options from server
            const beginResponse = await fetch('/api/passkey/register/begin', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({ passkeyName: passkeyName }),
                signal: signal
            });

            if (!beginResponse.ok) {
                throw new Error('Failed to begin registration');
            }

            const options = await beginResponse.json();

            // Convert challenge and user ID from base64
            options.challenge = this.base64ToArrayBuffer(options.challenge);
            options.user.id = this.base64ToArrayBuffer(options.user.id);

            // Convert excludeCredentials if present
            if (options.excludeCredentials) {
                options.excludeCredentials = options.excludeCredentials.map(cred => ({
                    ...cred,
                    id: this.base64ToArrayBuffer(cred.id)
                }));
            }
            
            // Ensure timeout is set (use server value or default to 180 seconds for cloud password managers)
            if (!options.timeout || options.timeout < 180000) {
                options.timeout = 180000;
            }

            // Call WebAuthn API with the abort signal
            const credential = await navigator.credentials.create({ 
                publicKey: options,
                signal: signal
            });

            if (!credential) {
                throw new Error('Credential creation was cancelled or failed');
            }

            // Prepare attestation response
            const attestationResponse = {
                id: credential.id,
                rawId: this.arrayBufferToBase64(credential.rawId),
                type: credential.type,
                response: {
                    clientDataJson: this.arrayBufferToBase64(credential.response.clientDataJSON),
                    attestationObject: this.arrayBufferToBase64(credential.response.attestationObject)
                }
            };

            // Complete registration
            const completeResponse = await fetch('/api/passkey/register/complete', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    passkeyName: passkeyName,
                    attestationResponse: attestationResponse
                }),
                signal: signal
            });

            if (!completeResponse.ok) {
                throw new Error('Failed to complete registration');
            }
            
            // Clear the timeout on success
            clearTimeout(timeoutId);

            // Reload the page to show the new passkey
            window.location.reload();
        } catch (error) {
            // Clear the timeout on error
            clearTimeout(timeoutId);
            
            console.error('Error registering passkey:', error);
            
            // Provide more helpful error messages
            let userMessage = 'Failed to register passkey: ';
            
            if (error.name === 'AbortError') {
                // This could be from our timeout or user cancellation
                userMessage += 'The operation timed out or was cancelled. If you were trying to use a cloud password manager (like Google Password Manager), it may be having connectivity issues. Please try again or use a different option like Windows Hello or a security key.';
            } else if (error.name === 'NotAllowedError') {
                // Check if this might be a cloud password manager connectivity issue
                if (error.message && error.message.includes('timed out')) {
                    userMessage += 'The operation timed out. If you were trying to use a cloud password manager (like Google Password Manager), it may be having connectivity issues. Please try again or use a different option like Windows Hello or a security key.';
                } else {
                    userMessage += 'The operation was cancelled or not allowed. This can happen if:\n• You cancelled the operation\n• Your cloud password manager (e.g., Google Password Manager) cannot be reached\n• Your browser blocked the request\n\nPlease try again, or try using Windows Hello or a security key instead.';
                }
            } else if (error.name === 'NetworkError') {
                userMessage += 'Network error. Please check your internet connection and try again.';
            } else if (error.name === 'InvalidStateError') {
                userMessage += 'This passkey may already be registered. Please try with a different authenticator.';
            } else if (error.name === 'NotSupportedError') {
                userMessage += 'Passkeys are not supported by your browser or device.';
            } else if (error.message.includes('complete registration')) {
                userMessage += 'Registration could not be completed on the server. Please try again.';
            } else {
                userMessage += error.message || 'An unknown error occurred. Please try again.';
            }
            
            alert(userMessage);
        }
    },

    loginWithPasskey: async function (username) {
        try {
            // Begin login - get options from server
            const beginResponse = await fetch('/api/passkey/login/begin', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({ username: username })
            });

            if (!beginResponse.ok) {
                throw new Error('Failed to begin login');
            }

            const options = await beginResponse.json();

            // Convert challenge from base64
            options.challenge = this.base64ToArrayBuffer(options.challenge);

            // Convert allowCredentials
            if (options.allowCredentials) {
                options.allowCredentials = options.allowCredentials.map(cred => ({
                    ...cred,
                    id: this.base64ToArrayBuffer(cred.id)
                }));
            }

            // Call WebAuthn API
            const assertion = await navigator.credentials.get({ publicKey: options });

            if (!assertion) {
                throw new Error('Failed to get assertion');
            }

            // Prepare assertion response
            const assertionResponse = {
                id: assertion.id,
                rawId: this.arrayBufferToBase64(assertion.rawId),
                type: assertion.type,
                response: {
                    clientDataJson: this.arrayBufferToBase64(assertion.response.clientDataJSON),
                    authenticatorData: this.arrayBufferToBase64(assertion.response.authenticatorData),
                    signature: this.arrayBufferToBase64(assertion.response.signature),
                    userHandle: assertion.response.userHandle ? this.arrayBufferToBase64(assertion.response.userHandle) : null
                }
            };

            // Complete login
            const completeResponse = await fetch('/api/passkey/login/complete', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(assertionResponse)
            });

            if (!completeResponse.ok) {
                throw new Error('Failed to complete login');
            }

            // Redirect to home page on successful login
            window.location.href = '/';
        } catch (error) {
            console.error('Error logging in with passkey:', error);
            
            // Provide more helpful error messages
            let userMessage = 'Failed to login with passkey: ';
            
            if (error.name === 'NotAllowedError') {
                userMessage += 'The operation was cancelled or not allowed. Please try again.';
            } else if (error.name === 'AbortError') {
                userMessage += 'The operation was aborted. This may happen if your browser cannot reach the password manager service. Please check your internet connection and try again.';
            } else if (error.name === 'NetworkError') {
                userMessage += 'Network error. Please check your internet connection and try again.';
            } else if (error.name === 'InvalidStateError') {
                userMessage += 'No matching passkey found. Please try a different authentication method.';
            } else if (error.name === 'NotSupportedError') {
                userMessage += 'Passkeys are not supported by your browser or device.';
            } else {
                userMessage += error.message || 'An unknown error occurred. Please try again.';
            }
            
            alert(userMessage);
        }
    },

    // Helper functions for base64 encoding/decoding
    base64ToArrayBuffer: function (base64) {
        // Add padding if necessary
        base64 = base64.replace(/-/g, '+').replace(/_/g, '/');
        while (base64.length % 4) {
            base64 += '=';
        }
        const binaryString = atob(base64);
        const bytes = new Uint8Array(binaryString.length);
        for (let i = 0; i < binaryString.length; i++) {
            bytes[i] = binaryString.charCodeAt(i);
        }
        return bytes.buffer;
    },

    arrayBufferToBase64: function (buffer) {
        const bytes = new Uint8Array(buffer);
        let binary = '';
        for (let i = 0; i < bytes.byteLength; i++) {
            binary += String.fromCharCode(bytes[i]);
        }
        return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
    }
};
