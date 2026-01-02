let paypalLoaded = false;
let currentOrderId = null;

export async function initPayPal(clientId, sellerMerchantId, amount, dotNetRef) {
    if (!clientId || clientId === '__REPLACE_WITH_PAYPAL_CLIENT_ID__') {
        console.log('PayPal client ID not configured');
        return;
    }

    // Load PayPal SDK if not already loaded
    if (!paypalLoaded) {
        await loadPayPalScript(clientId, sellerMerchantId);
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
                
                // Call server to create order (returns internal ID or PayPal ID)
                const orderId = await dotNetRef.invokeMethodAsync('CreateOrder');
                console.log('Order created, returned ID:', orderId);
                
                if (!orderId) {
                    throw new Error('Failed to create order');
                }

                // Store the order ID for use in onApprove
                currentOrderId = orderId;

                // For multi-party orders with seller merchant-id in SDK,
                // the server pre-creates the PayPal order and returns its ID
                // We just return it directly without creating a new one
                if (sellerMerchantId) {
                    console.log('Multi-party order: Using server-created PayPal order ID:', orderId);
                    return orderId;
                }

                // For standard orders, create PayPal order client-side
                console.log('Standard order: Creating PayPal order client-side');
                const paypalOrderId = await actions.order.create({
                    purchase_units: [{
                        reference_id: orderId,
                        amount: {
                            value: amount,
                            currency_code: 'USD'
                        }
                    }],
                    // Enable 3D Secure authentication when required
                    // Note: This applies to card payments only. PayPal wallet and other
                    // payment methods have their own authentication mechanisms.
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
                
                console.log('PayPal order created client-side with 3D Secure support:', paypalOrderId);
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
                
                // Note: 3D Secure authentication happens during the approval flow (before this callback)
                // The payment_source configuration in createOrder triggers 3D Secure when required
                // We do NOT call actions.order.capture() here because the popup may be closed
                // Server will capture the payment after we notify it
                console.log('Payment approved (3D Secure passed if required), notifying server to capture...');
                
                // Use our stored internal order ID
                const internalOrderId = currentOrderId || (data && data.orderID);
                const paypalOrderId = data && data.orderID;
                console.log('Using internal order ID:', internalOrderId);
                console.log('PayPal order ID:', paypalOrderId);
                
                if (!internalOrderId || !paypalOrderId) {
                    throw new Error('Missing order identifiers');
                }

                // Notify server of successful payment approval - server will capture
                await dotNetRef.invokeMethodAsync('OnApprove', { orderId: internalOrderId, payPalOrderId: paypalOrderId });
                console.log('Server notified of payment approval');
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

function loadPayPalScript(clientId, sellerMerchantId) {
    return new Promise((resolve, reject) => {
        if (document.querySelector('script[src*="paypal.com/sdk"]')) {
            resolve();
            return;
        }

        const script = document.createElement('script');
        // Build SDK URL with client-id and optional merchant-id for multi-party
        // Add enable-funding and intent parameters for Expanded Checkout
        // enable-funding=venmo,paylater adds additional payment options
        // intent=capture ensures immediate payment capture
        let sdkUrl = `https://www.paypal.com/sdk/js?client-id=${clientId}&currency=USD&enable-funding=venmo,paylater&intent=capture`;
        
        // For multi-party payments, add the seller's merchant ID
        // This allows the SDK to work with orders where the seller is the payee
        if (sellerMerchantId) {
            sdkUrl += `&merchant-id=${sellerMerchantId}`;
            console.log('Loading PayPal SDK with seller merchant ID for multi-party payment');
        }
        
        script.src = sdkUrl;
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
