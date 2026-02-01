/**
 * Avalara Form Helper - JavaScript helper for Avalara 1099/W-9 embedded form requests
 * 
 * This helper works with the Avalara Track1099 API to open embedded W-9/W-8BEN forms
 * within a modal dialog.
 */

// Form type constants
const FORM_TYPE_W9 = 'W-9';
const FORM_TYPE_W8BEN = 'W-8BEN';
const FORM_TYPE_W8BEN_E = 'W-8BEN-E';

window.avalaraFormHelper = {
    /**
     * Opens the Avalara embedded form request modal.
     * @param {string} formRequestJson - The JSON response from the form_requests API endpoint
     */
    openFormRequest: function (formRequestJson) {
        try {
            // Parse the form request JSON
            const formRequest = JSON.parse(formRequestJson);
            
            // Validate the form request structure
            if (!formRequest || !formRequest.data) {
                throw new Error('Invalid form request structure');
            }
            
            const data = formRequest.data;
            if (!data.attributes?.company_id || !data.links?.action_complete) {
                throw new Error('Form request is missing required fields');
            }

            // Check if Avalara1099 SDK is available
            if (typeof Avalara1099 === 'undefined') {
                throw new Error('Avalara1099 SDK is not loaded. Please refresh the page and try again.');
            }

            // Determine the form type from the response to call the appropriate SDK method
            const formType = data.attributes?.form_type;
            if (!formType) {
                console.warn('Form type not specified in response, defaulting to W-9');
            }
            const isW8Form = formType === FORM_TYPE_W8BEN || formType === FORM_TYPE_W8BEN_E;
            
            // Call the appropriate Avalara SDK method based on form type
            // The SDK will display a modal dialog where the user can complete and sign their form
            const sdkOptions = {
                // Optional prefill values can be added here if needed
                prefill: {},
                // Uncomment to disable "Are you sure?" dialog when closing
                // skipCloseConfirmation: true
            };
            
            // Use requestW8 for W-8BEN and W-8BEN-E forms, requestW9 for W-9 forms
            const sdkPromise = isW8Form 
                ? Avalara1099.requestW8(formRequest, sdkOptions)
                : Avalara1099.requestW9(formRequest, sdkOptions);
            
            sdkPromise
            .then(async newRequest => {
                // Form was completed and signed successfully
                const attributes = (newRequest.data || newRequest).attributes;
                const signedAt = attributes.signed_at;
                
                if (signedAt) {
                    // The form was signed - notify our backend
                    try {
                        const response = await fetch('/api/avalara/w9-complete', {
                            method: 'POST',
                            headers: {
                                'Content-Type': 'application/json'
                            },
                            body: JSON.stringify(newRequest)
                        });
                        
                        if (response.ok) {
                            alert('Your tax form has been submitted successfully! The page will reload to update your status.');
                        } else {
                            console.error('Failed to notify backend of form completion:', response.status);
                            alert('Your tax form has been submitted. Please wait a few moments and refresh the page to see your updated status.');
                        }
                    } catch (err) {
                        console.error('Error notifying backend:', err);
                        alert('Your tax form has been submitted. Please wait a few moments and refresh the page to see your updated status.');
                    }
                    window.location.reload();
                } else {
                    // The form was not completed (possibly saved as draft)
                    alert('Your tax form progress has been saved. Please complete and sign the form to activate your creator account.');
                    window.location.reload();
                }
            })
            .catch(errors => {
                console.log('Avalara form errors:', errors);
                
                if (errors === 'cancel') {
                    // User closed the form without completing it
                    console.log('User cancelled the form');
                    return;
                }
                
                if (errors?.errors?.[0]?.status === '404') {
                    alert('Session timed out. Please refresh the page and try again.');
                    window.location.reload();
                    return;
                }
                
                // Other error - log it and show generic message
                console.error('Avalara form error:', errors);
                alert('Something went wrong while processing your tax form. Please try again.');
            });
            
        } catch (error) {
            console.error('Error opening Avalara form:', error);
            alert('Error opening tax form: ' + error.message);
        }
    }
};
