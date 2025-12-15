window.passkeyHelper = {
    registerPasskey: async function (passkeyName, userId) {
        try {
            // Begin registration - get options from server
            const beginResponse = await fetch('/api/passkey/register/begin', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({ passkeyName: passkeyName })
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

            // Call WebAuthn API
            const credential = await navigator.credentials.create({ publicKey: options });

            if (!credential) {
                throw new Error('Failed to create credential');
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
                })
            });

            if (!completeResponse.ok) {
                throw new Error('Failed to complete registration');
            }

            // Reload the page to show the new passkey
            window.location.reload();
        } catch (error) {
            console.error('Error registering passkey:', error);
            alert('Failed to register passkey: ' + error.message);
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
            alert('Failed to login with passkey: ' + error.message);
        }
    },

    // Helper functions for base64 encoding/decoding
    base64ToArrayBuffer: function (base64) {
        const binaryString = atob(base64.replace(/-/g, '+').replace(/_/g, '/'));
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
