/**
 * TaxBandits Drop-in UI integration for embedded W-9/W-8 tax form.
 * Loads the TaxBandits dropinWhCertificate.js script dynamically and initializes the form.
 * See: https://developer.taxbandits.com/docs/WhCertificate/Authentication_Setup
 */

let dotNetRef = null;

/**
 * Dynamically loads the TaxBandits Drop-in script.
 * @param {string} scriptUrl - The URL of the TaxBandits Drop-in script.
 * @returns {Promise} Resolves when the script is loaded.
 */
function loadTaxBanditsScript(scriptUrl) {
    return new Promise((resolve, reject) => {
        // Check if loadFormWH is already available from a previous load
        if (typeof loadFormWH === 'function') {
            console.log('[TaxForm] TaxBandits script already loaded');
            resolve();
            return;
        }
        console.log('[TaxForm] Loading TaxBandits script from:', scriptUrl);
        const script = document.createElement('script');
        script.src = scriptUrl;
        script.onload = () => {
            console.log('[TaxForm] TaxBandits script loaded successfully');
            resolve();
        };
        script.onerror = () => {
            console.error('[TaxForm] Failed to load TaxBandits script from:', scriptUrl);
            reject(new Error('Failed to load TaxBandits script'));
        };
        document.head.appendChild(script);
    });
}

/**
 * Initializes the TaxBandits Drop-in UI tax form using loadFormWH().
 * @param {string} transientToken - The transient token from the server.
 * @param {string} payeeRef - The PayeeRef (email) for the recipient.
 * @param {string} businessId - The TaxBandits BusinessId.
 * @param {string} scriptUrl - The URL of the TaxBandits Drop-in script.
 * @param {string} returnUrl - The URL to redirect to after form completion.
 * @param {object} dotNetObjRef - .NET object reference for callbacks.
 */
export async function initTaxForm(transientToken, payeeRef, businessId, scriptUrl, returnUrl, dotNetObjRef) {
    dotNetRef = dotNetObjRef;

    console.log('[TaxForm] Initializing tax form with:',
        'businessId:', businessId,
        'scriptUrl:', scriptUrl,
        'returnUrl:', returnUrl,
        'tokenLength:', transientToken ? transientToken.length : 0);

    try {
        await loadTaxBanditsScript(scriptUrl);

        // Listen for messages from the TaxBandits iframe
        window.addEventListener('message', handleTaxBanditsMessage);

        // Build the payload per TaxBandits API documentation
        const payLoad = {
            Requester: {
                BusinessId: businessId
            },
            Recipient: {
                PayeeRef: payeeRef,
                IsTINMatching: false
            },
            RedirectUrls: {
                ReturnUrl: returnUrl,
                CancelUrl: returnUrl
            }
        };

        console.log('[TaxForm] Calling loadFormWH with payload keys:', Object.keys(payLoad));

        // loadFormWH(transientToken, payLoad) is the API from dropinWhCertificate.js
        // Don't await — loadFormWH renders an iframe and may not resolve until the user
        // completes/cancels the form, which would cause Blazor's JS interop to time out.
        loadFormWH(transientToken, payLoad).then(() => {
            console.log('[TaxForm] loadFormWH completed successfully');
        }).catch((err) => {
            console.error('[TaxForm] loadFormWH error:', err);
        });
    } catch (error) {
        console.error('[TaxForm] Error initializing TaxBandits form:', error);
    }
}

/**
 * Handles messages from the TaxBandits iframe.
 */
function handleTaxBanditsMessage(event) {
    // TaxBandits sends messages from the iframe about form status
    if (event.data && dotNetRef) {
        try {
            const data = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
            console.log('[TaxForm] Received message from iframe:', JSON.stringify(data));
            if (data.status === 'completed' || data.status === 'cancelled') {
                dotNetRef.invokeMethodAsync('OnTaxFormComplete', data.status);
            }
        } catch {
            // Not a JSON message or not from TaxBandits, ignore
        }
    }
}
