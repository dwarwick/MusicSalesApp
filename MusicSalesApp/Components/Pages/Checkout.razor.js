let paypalLoaded = false;
let currentOrderId = null;
let currentPayPalOrderId = null;

export async function initPayPal(clientId, sellerMerchantIds, amount, dotNetRef) {
    if (!clientId || clientId === '__REPLACE_WITH_PAYPAL_CLIENT_ID__') {
        console.log('PayPal client ID not configured');
        return;
    }

    // Load PayPal SDK if not already loaded
    if (!paypalLoaded) {
        await loadPayPalScript(clientId, sellerMerchantIds);
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
                
                // Call server to create order (returns internal ID or object with both IDs)
                const orderResponse = await dotNetRef.invokeMethodAsync('CreateOrder');
                console.log('Order created, returned response:', orderResponse);
                
                if (!orderResponse) {
                    throw new Error('Failed to create order');
                }

                // Handle both string (standard) and object (multi-party) responses
                let internalOrderId;
                let paypalOrderId;
                let isMultiParty = false;

                if (typeof orderResponse === 'object' && orderResponse !== null && 'payPalOrderId' in orderResponse) {
                    // Multi-party order - response contains both IDs
                    internalOrderId = orderResponse.orderId;
                    paypalOrderId = orderResponse.payPalOrderId;
                    isMultiParty = orderResponse.isMultiParty;
                    console.log('Multi-party order detected:', { internalOrderId, paypalOrderId });
                } else {
                    // Standard order - response is just the internal ID
                    internalOrderId = orderResponse;
                    console.log('Standard order, internal ID:', internalOrderId);
                }

                // Store INTERNAL order ID for use in onApprove
                currentOrderId = internalOrderId;
                currentPayPalOrderId = paypalOrderId;

                // For multi-party orders with seller merchant-id(s) in SDK,
                // return the server-created PayPal order ID
                if (isMultiParty && paypalOrderId) {
                    console.log('Multi-party order: Using server-created PayPal order ID:', paypalOrderId);
                    return paypalOrderId;
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
                
                // Use our stored internal order ID and PayPal order ID
                const internalOrderId = currentOrderId;
                const paypalOrderId = currentPayPalOrderId || (data && data.orderID);
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
            currentPayPalOrderId = null;
            dotNetRef.invokeMethodAsync('OnCancel');
        },

        onError: function (err) {
            console.error('PayPal button error:', err);
            currentOrderId = null;
            currentPayPalOrderId = null;
            dotNetRef.invokeMethodAsync('OnError', err.toString());
        }
    }).render('#paypal-button-container');
}

function loadPayPalScript(clientId, sellerMerchantIds) {
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
        
        // For multi-party payments, add the seller merchant IDs (comma-separated for multiple sellers)
        // This allows the SDK to work with orders where sellers are the payees
        // Supports up to 10 merchants
        if (sellerMerchantIds) {
            sdkUrl += `&merchant-id=${sellerMerchantIds}`;
            const sellerCount = sellerMerchantIds.split(',').length;
            console.log(`Loading PayPal SDK with ${sellerCount} seller merchant ID(s) for multi-party payment`);
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
