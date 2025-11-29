namespace MusicSalesApp.Services
{
    public interface IEmailService
    {
        bool SendEmailVerificationMessage(string email, string tokenUrl);

        bool SendPasswordResetEmail(string email, string tokenUrl);

        Task<bool> SendEmailAsync(string toEmail, string subject, string body);
    }
}