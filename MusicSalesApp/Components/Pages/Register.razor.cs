using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Components.Base;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Pages;

public partial class RegisterModel : BlazorBase, IDisposable
{
    [SupplyParameterFromQuery(Name = "needsVerification")]
    public bool NeedsVerification { get; set; }

    [SupplyParameterFromQuery(Name = "email")]
    public string QueryEmail { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
    [Required]
    public string Password { get; set; } = string.Empty;
    [Required]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string NewEmail { get; set; } = string.Empty;

    // Legal agreement checkboxes
    public bool AcceptTermsOfUse { get; set; } = false;
    public bool AcceptPrivacyPolicy { get; set; } = false;

    // Computed property to check if user can register
    protected bool CanRegister => AcceptTermsOfUse && AcceptPrivacyPolicy;

    // Dialog references
    protected SfDialog _termsDialog = default!;
    protected SfDialog _privacyDialog = default!;

    // Legal document content
    protected string _termsOfUseContent = string.Empty;
    protected string _privacyPolicyContent = string.Empty;

    protected string errorMessage = string.Empty;
    protected string successMessage = string.Empty;
    protected string infoMessage = string.Empty;
    protected bool isSubmitting = false;
    protected bool showVerificationSection = false;
    protected bool canResendEmail = false;
    protected int secondsRemaining = 0;
    private System.Timers.Timer countdownTimer = null!;
    private bool disposed = false;

    protected override async Task OnInitializedAsync()
    {
        // Load legal document content
        LoadLegalDocuments();

        var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (auth.User.Identity != null && auth.User.Identity.IsAuthenticated)
        {
            // Check if authenticated but email not verified
            var emailClaim = auth.User.FindFirst(System.Security.Claims.ClaimTypes.Email);
            if (emailClaim != null)
            {
                var isVerified = await AuthenticationService.IsEmailVerifiedAsync(emailClaim.Value);
                if (!isVerified)
                {
                    Email = emailClaim.Value;
                    NewEmail = Email;
                    showVerificationSection = true;
                    infoMessage = "Your email address is not verified. Please verify your email to get full access to the site.";
                    await CheckResendAvailability();
                    return;
                }
            }
            NavigationManager.NavigateTo("/", forceLoad: true);
            return;
        }

        // Handle redirect from login for unverified users
        if (NeedsVerification && !string.IsNullOrEmpty(QueryEmail))
        {
            Email = QueryEmail;
            NewEmail = Email;
            showVerificationSection = true;
            infoMessage = "Your email address is not verified. Please verify your email to get full access to the site.";
            await CheckResendAvailability();
        }
    }

    private void LoadLegalDocuments()
    {
        // Terms of Use content
        _termsOfUseContent = GetTermsOfUseHtml();

        // Privacy Policy content
        _privacyPolicyContent = GetPrivacyPolicyHtml();
    }

    protected async Task ShowTermsOfUse()
    {
        await _termsDialog.ShowAsync();
    }

    protected async Task CloseTermsDialog()
    {
        await _termsDialog.HideAsync();
    }

    protected async Task ShowPrivacyPolicy()
    {
        await _privacyDialog.ShowAsync();
    }

    protected async Task ClosePrivacyDialog()
    {
        await _privacyDialog.HideAsync();
    }

    protected async Task HandleSubmit(EditContext context)
    {
        errorMessage = string.Empty;
        successMessage = string.Empty;
        infoMessage = string.Empty;

        if (isSubmitting) return;
        isSubmitting = true;
        try
        {
            if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password) || string.IsNullOrWhiteSpace(ConfirmPassword))
            {
                errorMessage = "All fields are required";
                return;
            }
            if (!new EmailAddressAttribute().IsValid(Email))
            {
                errorMessage = "Invalid email format";
                return;
            }
            if (Password != ConfirmPassword)
            {
                errorMessage = "Passwords do not match";
                return;
            }
            if (!AcceptTermsOfUse || !AcceptPrivacyPolicy)
            {
                errorMessage = "You must accept the Terms of Use and Privacy Policy to register";
                return;
            }
            // Call registration service
            var (success, err) = await AuthenticationService.RegisterAsync(Email, Password);
            if (!success)
            {
                errorMessage = err;
                return;
            }

            // Send verification email
            var baseUrl = NavigationManager.BaseUri;
            var (emailSent, emailError) = await AuthenticationService.SendVerificationEmailAsync(Email, baseUrl);
            
            if (!emailSent)
            {
                errorMessage = $"Account created but failed to send verification email: {emailError}";
                return;
            }

            // Show verification section
            NewEmail = Email;
            showVerificationSection = true;
            successMessage = "Registration successful! Please check your email to verify your account.";
            await CheckResendAvailability();
            StartCountdownTimer();
        }
        finally
        {
            isSubmitting = false;
            StateHasChanged();
        }
    }

    protected async Task ResendVerificationEmail()
    {
        if (isSubmitting) return;
        
        errorMessage = string.Empty;
        successMessage = string.Empty;
        infoMessage = string.Empty;
        isSubmitting = true;

        try
        {
            var baseUrl = NavigationManager.BaseUri;
            var (success, error) = await AuthenticationService.SendVerificationEmailAsync(Email, baseUrl);
            
            if (success)
            {
                successMessage = "Verification email sent! Please check your inbox.";
                await CheckResendAvailability();
                StartCountdownTimer();
            }
            else
            {
                errorMessage = error;
            }
        }
        finally
        {
            isSubmitting = false;
            StateHasChanged();
        }
    }

    protected async Task ChangeEmail()
    {
        if (isSubmitting) return;
        
        errorMessage = string.Empty;
        successMessage = string.Empty;
        infoMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(NewEmail))
        {
            errorMessage = "Please enter a new email address";
            return;
        }

        if (!new EmailAddressAttribute().IsValid(NewEmail))
        {
            errorMessage = "Invalid email format";
            return;
        }

        if (NewEmail.Equals(Email, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Please enter a different email address";
            return;
        }

        isSubmitting = true;
        try
        {
            var baseUrl = NavigationManager.BaseUri;
            var (success, error) = await AuthenticationService.UpdateEmailAsync(Email, NewEmail, baseUrl);
            
            if (success)
            {
                Email = NewEmail;
                successMessage = $"Email updated to {NewEmail}. A new verification email has been sent.";
                await CheckResendAvailability();
                StartCountdownTimer();
            }
            else
            {
                errorMessage = error;
            }
        }
        finally
        {
            isSubmitting = false;
            StateHasChanged();
        }
    }

    private async Task CheckResendAvailability()
    {
        var (canResend, remaining) = await AuthenticationService.CanResendVerificationEmailAsync(Email);
        canResendEmail = canResend;
        secondsRemaining = remaining;
    }

    private void StartCountdownTimer()
    {
        countdownTimer?.Stop();
        countdownTimer?.Dispose();
        countdownTimer = null;
        
        if (secondsRemaining > 0)
        {
            countdownTimer = new System.Timers.Timer(1000);
            countdownTimer.Elapsed += async (sender, e) =>
            {
                secondsRemaining--;
                if (secondsRemaining <= 0)
                {
                    canResendEmail = true;
                    countdownTimer?.Stop();
                }
                await InvokeAsync(StateHasChanged);
            };
            countdownTimer.Start();
        }
    }

    /// <summary>
    /// Formats remaining time for display (compact format).
    /// </summary>
    protected string FormatRemainingTime(int seconds)
    {
        var minutes = seconds / 60;
        var secs = seconds % 60;
        return minutes > 0 ? $"{minutes}m {secs}s" : $"{secs}s";
    }

    /// <summary>
    /// Formats remaining time for accessibility/screen readers (full descriptive format).
    /// </summary>
    protected string FormatRemainingTimeForAccessibility(int seconds)
    {
        var minutes = seconds / 60;
        var secs = seconds % 60;
        return minutes > 0 
            ? $"{minutes} minute{(minutes != 1 ? "s" : "")} and {secs} second{(secs != 1 ? "s" : "")}"
            : $"{secs} second{(secs != 1 ? "s" : "")}";
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                countdownTimer?.Stop();
                countdownTimer?.Dispose();
                countdownTimer = null;
            }
            disposed = true;
        }
    }

    private static string GetTermsOfUseHtml()
    {
        return """
            <div class="terms-content">
                <p class="text-muted"><strong>Last Updated:</strong> December 28, 2024</p>

                <section class="mb-4">
                    <h3>1. Agreement to Terms</h3>
                    <p>
                        By accessing or using the Streamtunes website at <a href="https://streamtunes.net" target="_blank">https://streamtunes.net</a> 
                        (the "Service"), you agree to be bound by these Terms of Use (the "Terms"). If you do not agree to these Terms, 
                        you may not access or use the Service.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>2. About Streamtunes</h3>
                    <p>
                        Streamtunes is a music streaming and sales platform operating and registered in the State of Nevada. 
                        The Service provides access to a library of music content generated using artificial intelligence technology (ChatGPT). 
                        All music available on the Service is owned by the operator, who has obtained all necessary rights to the content 
                        through its creation process.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>3. Account Registration and Types</h3>
                    
                    <h4>3.1 Free Account</h4>
                    <p>
                        You may register for a free account on the Service. With a free account, you can:
                    </p>
                    <ul>
                        <li>Preview the first 60 seconds of any song in our library</li>
                        <li>Like and dislike songs to personalize your experience</li>
                        <li>Browse our music library</li>
                    </ul>
                    <p>
                        Free accounts do not provide access to full-length songs unless you purchase them individually or subscribe 
                        to our monthly subscription service.
                    </p>

                    <h4>3.2 Guest Access</h4>
                    <p>
                        Even without registering for an account, visitors to the Service can preview the first 60 seconds of any song. 
                        However, guest users cannot like/dislike songs, create playlists, purchase music, or subscribe to the Service.
                    </p>

                    <h4>3.3 Account Security</h4>
                    <p>
                        You are responsible for maintaining the confidentiality of your account credentials and for all activities 
                        that occur under your account. You agree to notify us immediately of any unauthorized use of your account.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>4. Music Preview Policy</h3>
                    <p>
                        All users, whether registered or not, can preview the first 60 seconds of any song available on the Service 
                        at no charge. This preview functionality is provided to help you evaluate music before making a purchase or 
                        subscribing to the Service.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>5. Purchases</h3>
                    
                    <h4>5.1 Individual Song and Album Purchases</h4>
                    <p>
                        You may purchase individual songs or complete albums through the Service. Once purchased, you will have 
                        permanent access to the full-length version of the purchased content as long as your account remains active 
                        and the Service continues to operate.
                    </p>

                    <h4>5.2 Payment Processing</h4>
                    <p>
                        All payments are processed securely through PayPal. By making a purchase, you agree to PayPal's terms of service. 
                        Prices are displayed in U.S. Dollars (USD) and are subject to change at our discretion.
                    </p>

                    <h4>5.3 No Refunds</h4>
                    <p>
                        All sales are final. We do not offer refunds for individual song or album purchases. We encourage you to use 
                        the 60-second preview feature before making any purchase decisions.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>6. Monthly Subscription</h3>
                    
                    <h4>6.1 Subscription Features</h4>
                    <p>
                        By subscribing to our monthly subscription service, you gain unlimited streaming access to all songs in our library 
                        for the duration of your active subscription.
                    </p>

                    <h4>6.2 Billing</h4>
                    <p>
                        Subscriptions are billed monthly through PayPal on a recurring basis. Your subscription will automatically renew 
                        each month unless you cancel it.
                    </p>

                    <h4>6.3 Cancellation</h4>
                    <p>
                        You may cancel your subscription at any time through your account management page. Your subscription will remain 
                        active until the end of your current billing period.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>7. Account Deletion</h3>
                    <p>
                        You may delete your account at any time through your account management page. All your account data will be 
                        permanently deleted, including your purchase history and playlists. This action cannot be undone.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>8. Prohibited Uses</h3>
                    <p>You agree not to:</p>
                    <ul>
                        <li>Use the Service for any illegal purpose or in violation of any laws</li>
                        <li>Attempt to circumvent any technological measures we use to protect the Service or content</li>
                        <li>Download, copy, reproduce, distribute, or create derivative works from any content except as expressly permitted</li>
                        <li>Share your account credentials with others or allow others to use your account</li>
                    </ul>
                </section>

                <section class="mb-4">
                    <h3>9. Intellectual Property Rights</h3>
                    <p>
                        All content available through the Service is the property of Streamtunes or its content suppliers and is protected 
                        by United States and international copyright laws. All music has been generated using artificial intelligence technology.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>10. Privacy and Data Collection</h3>
                    <p>
                        Your use of the Service is also governed by our Privacy Policy, which describes how we collect, use, and 
                        protect your personal information.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>11. Disclaimers and Limitation of Liability</h3>
                    <p>
                        THE SERVICE IS PROVIDED ON AN "AS IS" AND "AS AVAILABLE" BASIS WITHOUT WARRANTIES OF ANY KIND, EITHER 
                        EXPRESS OR IMPLIED.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>12. Governing Law</h3>
                    <p>
                        These Terms shall be governed by and construed in accordance with the laws of the State of Nevada, 
                        without regard to its conflict of law provisions.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>13. Contact Information</h3>
                    <p>
                        <strong>Streamtunes</strong><br />
                        Nevada, United States<br />
                        Website: <a href="https://streamtunes.net" target="_blank">https://streamtunes.net</a><br />
                        Email: <a href="mailto:customerservice@streamtunes.net">customerservice@streamtunes.net</a>
                    </p>
                </section>

                <section class="mb-4">
                    <h3>14. Acknowledgment</h3>
                    <p>
                        BY USING THE SERVICE, YOU ACKNOWLEDGE THAT YOU HAVE READ THESE TERMS OF USE, UNDERSTAND THEM, AND AGREE 
                        TO BE BOUND BY THEM.
                    </p>
                </section>
            </div>
            """;
    }

    private static string GetPrivacyPolicyHtml()
    {
        return """
            <div class="privacy-content">
                <p class="text-muted"><strong>Last Updated:</strong> December 28, 2024</p>

                <section class="mb-4">
                    <h3>1. Introduction</h3>
                    <p>
                        Welcome to Streamtunes. We are committed to protecting your privacy and personal information. This Privacy Policy 
                        explains how we collect, use, disclose, and safeguard your information when you use our music streaming and sales platform.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>2. Information We Collect</h3>
                    
                    <h4>2.1 Information You Provide Directly</h4>
                    <ul>
                        <li><strong>Email Address:</strong> Required for account registration, login, and communication.</li>
                        <li><strong>Password:</strong> Required for account security. Passwords are securely hashed and never stored in plain text.</li>
                        <li><strong>Passkey Credentials:</strong> If you use passkey authentication, we store public key credentials for secure passwordless login.</li>
                        <li><strong>Theme Preferences:</strong> Your selected display theme preference (Light or Dark mode).</li>
                    </ul>

                    <h4>2.2 Information Collected Automatically</h4>
                    <ul>
                        <li><strong>Usage Data:</strong> Information about how you interact with the Service, including songs you listen to, like, or purchase.</li>
                        <li><strong>Technical Data:</strong> Device information, browser type, and IP address collected through standard web server logs.</li>
                    </ul>

                    <h4>2.3 Information We Do NOT Collect</h4>
                    <ul>
                        <li>We do NOT collect your name, physical address, phone number, or date of birth.</li>
                        <li>We do NOT collect or store your complete payment card details (handled exclusively by PayPal).</li>
                        <li>We do NOT use cookies for third-party advertising or tracking.</li>
                    </ul>
                </section>

                <section class="mb-4">
                    <h3>3. Payment Processing and PayPal Integration</h3>
                    <p>
                        All payments are processed through PayPal. When you make a purchase, we share:
                    </p>
                    <ul>
                        <li><strong>Order Details:</strong> Purchase amount and order reference ID</li>
                        <li><strong>Currency:</strong> Transactions in U.S. Dollars (USD)</li>
                    </ul>
                    <p>
                        We receive from PayPal: Order ID, transaction status, subscription ID (for subscriptions), and payment timestamps.
                        We do not have access to your PayPal account credentials, bank account numbers, or complete credit card details.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>4. How We Use Your Information</h3>
                    <ul>
                        <li>Creating and maintaining your user account</li>
                        <li>Authenticating your identity when you log in</li>
                        <li>Sending email verification and password reset messages</li>
                        <li>Processing purchases and subscriptions</li>
                        <li>Providing access to music streaming and purchases</li>
                        <li>Improving the functionality and user experience</li>
                        <li>Protecting against fraudulent or unauthorized activity</li>
                    </ul>
                </section>

                <section class="mb-4">
                    <h3>5. Information Sharing and Disclosure</h3>
                    <p><strong>We Do NOT Sell Your Personal Information.</strong></p>
                    <p>We may share information with:</p>
                    <ul>
                        <li><strong>PayPal:</strong> Payment processing</li>
                        <li><strong>Email Service Providers:</strong> Sending transactional emails</li>
                        <li><strong>Cloud Hosting:</strong> Secure data storage and service hosting</li>
                        <li><strong>Legal Requirements:</strong> When required by law or to protect our rights</li>
                    </ul>
                </section>

                <section class="mb-4">
                    <h3>6. Data Security</h3>
                    <ul>
                        <li><strong>Password Security:</strong> Passwords are hashed using industry-standard cryptographic algorithms.</li>
                        <li><strong>Passkey Security:</strong> Uses public key cryptography; private keys never leave your device.</li>
                        <li><strong>Encryption:</strong> Data transmitted using HTTPS/TLS encryption.</li>
                        <li><strong>Access Controls:</strong> Access to personal information is restricted to authorized personnel.</li>
                    </ul>
                </section>

                <section class="mb-4">
                    <h3>7. Data Retention</h3>
                    <ul>
                        <li><strong>Active Accounts:</strong> Information retained while your account is active.</li>
                        <li><strong>Account Deletion:</strong> When you delete your account, we delete your personal information.</li>
                    </ul>
                </section>

                <section class="mb-4">
                    <h3>8. Your Rights and Choices</h3>
                    <ul>
                        <li><strong>Access:</strong> View your information through your account management page.</li>
                        <li><strong>Update:</strong> Update your email, password, and preferences in account settings.</li>
                        <li><strong>Delete:</strong> Delete your account at any time through account management.</li>
                        <li><strong>Passkeys:</strong> Add, remove, or rename passkeys at any time.</li>
                    </ul>
                </section>

                <section class="mb-4">
                    <h3>9. Children's Privacy</h3>
                    <p>
                        The Service is not directed to children under the age of 13. We do not knowingly collect personal information 
                        from children under 13.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>10. Changes to This Privacy Policy</h3>
                    <p>
                        We may update this Privacy Policy from time to time. We will notify you of material changes via email or 
                        prominent notice on the Service.
                    </p>
                </section>

                <section class="mb-4">
                    <h3>11. Contact Us</h3>
                    <p>
                        <strong>Streamtunes</strong><br />
                        Nevada, United States<br />
                        Website: <a href="https://streamtunes.net" target="_blank">https://streamtunes.net</a><br />
                        Email: <a href="mailto:customerservice@streamtunes.net">customerservice@streamtunes.net</a>
                    </p>
                </section>

                <section class="mb-4">
                    <h3>12. Acknowledgment</h3>
                    <p>
                        BY USING THE SERVICE, YOU ACKNOWLEDGE THAT YOU HAVE READ THIS PRIVACY POLICY, UNDERSTAND IT, AND AGREE 
                        TO BE BOUND BY ITS TERMS.
                    </p>
                </section>
            </div>
            """;
    }
}

