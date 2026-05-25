#nullable enable

namespace MusicSalesApp.Services;

public interface IContactRequestEmailService
{
    Task<ContactRequestEmailResult> SendContactRequestEmailsAsync(string userEmail, string subject, string message);
}

public sealed record ContactRequestEmailResult(bool UserEmailSent, bool AdminEmailSent)
{
    public bool Success => UserEmailSent && AdminEmailSent;
}