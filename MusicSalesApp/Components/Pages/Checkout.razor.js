let paypalLoaded = false;
let currentOrderId = null;

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
                console.log('PayPal createOrder called');
                
                // First, create our internal order record
                const orderId = await dotNetRef.invokeMethodAsync('CreateOrder');
                console.log('Internal order created:', orderId);
                
                if (!orderId) {
                    throw new Error('Failed to create internal order');
                }

                // Store the order ID for use in onApprove
                currentOrderId = orderId;

                // Create PayPal order using client-side SDK with 3D Secure support
                const paypalOrderId = await actions.order.create({
                    purchase_units: [{
                        reference_id: orderId,
                        amount: {
                            value: amount,
                            currency_code: 'USD'
                        }
                    }],
                    // Enable 3D Secure authentication when required
                    payment_source: {
                        card: {
                            attributes: {
                                verification: {
                                    method: 'SCA_ALWAYS' // Strong Customer Authentication (3D Secure)
                                }
                            }
                        }
                    }
                });
                
                console.log('PayPal order created with 3D Secure support:', paypalOrderId);
                return paypalOrderId;
            } catch (error) {
                console.error('Error creating order:', error);
                dotNetRef.invokeMethodAsync('OnError', error.message || error.toString());
                throw error;
            }
        },

        onApprove: async function (data, actions) {
            console.log('PayPal onApprove called, data:', data);
            
            try {
                // Show processing state
                await dotNetRef.invokeMethodAsync('SetProcessing', true);
                
                // Capture the order on the client side first to handle 3D Secure
                console.log('Capturing order on client side to handle 3D Secure authentication...');
                const captureResult = await actions.order.capture();
                console.log('Client-side capture result:', captureResult);

                // Check if 3D Secure authentication was required and completed
                if (captureResult.status === 'COMPLETED') {
                    console.log('Payment completed successfully (3D Secure passed if required)');
                    
                    // Use our stored internal order ID
                    const internalOrderId = currentOrderId || (data && data.orderID);
                    const paypalOrderId = data && data.orderID;
                    console.log('Using internal order ID:', internalOrderId);
                    console.log('PayPal order ID:', paypalOrderId);
                    
                    if (!internalOrderId || !paypalOrderId) {
                        throw new Error('Missing order identifiers');
                    }

                    // Notify server of successful payment approval
                    await dotNetRef.invokeMethodAsync('OnApprove', { orderId: internalOrderId, payPalOrderId: paypalOrderId });
                    console.log('Server notified of payment completion');
                } else {
                    throw new Error('Payment was not completed. Status: ' + captureResult.status);
                }
            } catch (error) {
                console.error('Error in onApprove:', error);
                await dotNetRef.invokeMethodAsync('OnError', error.message || error.toString());
            }
        },

        onCancel: function (data) {
            console.log('Payment cancelled by user');
            currentOrderId = null;
            dotNetRef.invokeMethodAsync('OnCancel');
        },

        onError: function (err) {
            console.error('PayPal button error:', err);
            currentOrderId = null;
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
        // Add enable-funding and intent parameters for Expanded Checkout
        // enable-funding=venmo,paylater adds additional payment options
        // intent=capture ensures immediate payment capture
        script.src = `https://www.paypal.com/sdk/js?client-id=${clientId}&currency=USD&enable-funding=venmo,paylater&intent=capture`;
        script.async = true;
        script.onload = () => {
            console.log('PayPal SDK loaded successfully with Expanded Checkout features');
            resolve();
        };
        script.onerror = (e) => {
            console.error('Failed to load PayPal SDK:', e);
            reject(e);
        };
        document.head.appendChild(script);
    });
}
