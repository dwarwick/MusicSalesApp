namespace MusicSalesApp.Services;

public sealed class GooglePlayVerificationException : Exception
{
    public GooglePlayVerificationException(string message, Exception innerException = null)
        : base(message, innerException)
    {
    }
}