namespace MusicSalesApp.Models;

/// <summary>
/// Represents the result of an email sending operation.
/// </summary>
public class EmailResult
{
    /// <summary>
    /// Indicates whether the email was sent successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The type of error if the email failed to send.
    /// </summary>
    public EmailErrorType ErrorType { get; init; }

    /// <summary>
    /// A user-friendly error message describing the failure.
    /// </summary>
    public string ErrorMessage { get; init; } = string.Empty;

    /// <summary>
    /// Creates a successful email result.
    /// </summary>
    public static EmailResult Succeeded() => new() { Success = true, ErrorType = EmailErrorType.None };

    /// <summary>
    /// Creates a failed email result due to spam filter rejection.
    /// </summary>
    public static EmailResult SpamFilterRejected() => new()
    {
        Success = false,
        ErrorType = EmailErrorType.SpamFilterRejected,
        ErrorMessage = "The email was rejected by the spam filter. Please try again later or contact support."
    };

    /// <summary>
    /// Creates a failed email result due to missing configuration.
    /// </summary>
    public static EmailResult MissingConfiguration() => new()
    {
        Success = false,
        ErrorType = EmailErrorType.MissingConfiguration,
        ErrorMessage = "Email service is not properly configured. Please contact support."
    };

    /// <summary>
    /// Creates a failed email result due to an SMTP error.
    /// </summary>
    /// <param name="message">The error message.</param>
    public static EmailResult SmtpError(string message) => new()
    {
        Success = false,
        ErrorType = EmailErrorType.SmtpError,
        ErrorMessage = message
    };

    /// <summary>
    /// Creates a failed email result due to an unexpected error.
    /// </summary>
    /// <param name="message">The error message.</param>
    public static EmailResult UnexpectedError(string message) => new()
    {
        Success = false,
        ErrorType = EmailErrorType.UnexpectedError,
        ErrorMessage = message
    };
}

/// <summary>
/// Types of email sending errors.
/// </summary>
public enum EmailErrorType
{
    /// <summary>
    /// No error occurred.
    /// </summary>
    None,

    /// <summary>
    /// The email was rejected by the server's spam filter.
    /// </summary>
    SpamFilterRejected,

    /// <summary>
    /// Email configuration is missing or invalid.
    /// </summary>
    MissingConfiguration,

    /// <summary>
    /// A general SMTP error occurred.
    /// </summary>
    SmtpError,

    /// <summary>
    /// An unexpected error occurred.
    /// </summary>
    UnexpectedError
}
