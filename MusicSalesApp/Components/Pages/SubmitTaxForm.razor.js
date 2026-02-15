/**
 * TaxBandits Drop-in UI integration for embedded W-9/W-8 tax form.
 * Loads the TaxBandits script dynamically and initializes the form.
 */

let dotNetRef = null;

/**
 * Dynamically loads the TaxBandits Drop-in script.
 * @param {boolean} useSandbox - Whether to use the sandbox environment.
 * @returns {Promise} Resolves when the script is loaded.
 */
function loadTaxBanditsScript(useSandbox) {
    return new Promise((resolve, reject) => {
        // Check if already loaded
        if (typeof LoadFormWhCertificate === 'function') {
            resolve();
            return;
        }
        const script = document.createElement('script');
        script.src = useSandbox
            ? 'https://js.taxbandits.io/SB/Web/Dropin/v1.0.0/dropinWhCertificate.js'
            : 'https://js.taxbandits.io/Web/Dropin/v1.0.0/dropinWhCertificate.js';
        script.onload = resolve;
        script.onerror = () => reject(new Error('Failed to load TaxBandits script'));
        document.head.appendChild(script);
    });
}

/**
 * Initializes the TaxBandits Drop-in UI tax form.
 * @param {string} transientToken - The transient token from the server.
 * @param {string} payeeRef - The PayeeRef (email) for the recipient.
 * @param {string} businessId - The TaxBandits BusinessId.
 * @param {boolean} useSandbox - Whether to use the sandbox environment.
 * @param {string} returnUrl - The URL to redirect to after form completion.
 * @param {object} dotNetObjRef - .NET object reference for callbacks.
 */
export async function initTaxForm(transientToken, payeeRef, businessId, useSandbox, returnUrl, dotNetObjRef) {
    dotNetRef = dotNetObjRef;

    try {
        await loadTaxBanditsScript(useSandbox);

        // Listen for messages from the TaxBandits iframe
        window.addEventListener('message', handleTaxBanditsMessage);

        // Call the TaxBandits LoadFormWhCertificate function
        LoadFormWhCertificate({
            TransientToken: transientToken,
            Requester: {
                BusinessId: businessId
            },
            PayeeRef: payeeRef,
            IsTINMatching: true,
            ReturnUrl: returnUrl,
            CancelUrl: returnUrl
        }, 'taxFormContainer');
    } catch (error) {
        console.error('Error initializing TaxBandits form:', error);
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
            if (data.status === 'completed' || data.status === 'cancelled') {
                dotNetRef.invokeMethodAsync('OnTaxFormComplete', data.status);
            }
        } catch {
            // Not a JSON message or not from TaxBandits, ignore
        }
    }
}
