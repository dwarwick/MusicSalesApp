let paypalLoaded = false;

export async function initPayPal(clientId, amount, dotNetRef) {
    if (!clientId || clientId === '__REPLACE_WITH_PAYPAL_CLIENT_ID__') {
        console.log('PayPal client ID not configured');
        return;
    }

    // Load PayPal SDK if not already loaded
    if (!paypalLoaded) {
        await loadPayPalScript(clientId);
        paypalLoaded = true;
    }

    // Clear any existing buttons
    const container = document.getElementById('paypal-button-container');
    if (container) {
        container.innerHTML = '';
    }

    // Wait for PayPal SDK to be ready
    if (typeof paypal === 'undefined') {
        console.log('PayPal SDK not available');
        return;
    }

    paypal.Buttons({
        style: {
            layout: 'vertical',
            color: 'gold',
            shape: 'rect',
            label: 'paypal'
        },

        createOrder: async function (data, actions) {
            try {
                const orderId = await dotNetRef.invokeMethodAsync('CreateOrder');
                if (!orderId) {
                    throw new Error('Failed to create order');
                }

                // Create PayPal order
                return actions.order.create({
                    purchase_units: [{
                        reference_id: orderId,
                        amount: {
                            value: amount
                        }
                    }]
                });
            } catch (error) {
                console.error('Error creating order:', error);
                dotNetRef.invokeMethodAsync('OnError', error.message);
                throw error;
            }
        },

        onApprove: async function (data, actions) {
            try {
                // Capture the order on PayPal side
                const details = await actions.order.capture();

                // Notify server of successful payment
                await dotNetRef.invokeMethodAsync('OnApprove', details.purchase_units[0].reference_id);
            } catch (error) {
                console.error('Error capturing order:', error);
                dotNetRef.invokeMethodAsync('OnError', error.message);
            }
        },

        onCancel: function (data) {
            console.log('Payment cancelled');
            dotNetRef.invokeMethodAsync('OnCancel');
        },

        onError: function (err) {
            console.error('PayPal error:', err);
            dotNetRef.invokeMethodAsync('OnError', err.toString());
        }
    }).render('#paypal-button-container');
}

function loadPayPalScript(clientId) {
    return new Promise((resolve, reject) => {
        if (document.querySelector('script[src*="paypal.com/sdk"]')) {
            resolve();
            return;
        }

        const script = document.createElement('script');
        script.src = `https://www.paypal.com/sdk/js?client-id=${clientId}&currency=USD`;
        script.async = true;
        script.onload = resolve;
        script.onerror = reject;
        document.head.appendChild(script);
    });
}
