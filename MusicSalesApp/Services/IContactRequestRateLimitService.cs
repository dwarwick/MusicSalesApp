#nullable enable

namespace MusicSalesApp.Services;

public interface IContactRequestRateLimitService
{
    Task<ContactRequestReservationResult> TryReserveSubmissionAsync(
        int userId,
        string userEmail,
        string subject,
        int messageLength,
        string? ipAddress);

    Task MarkEmailResultAsync(int submissionId, bool userEmailSent, bool adminEmailSent);
}

public sealed record ContactRequestReservationResult(bool IsAllowed, int? SubmissionId, string? ErrorMessage)
{
    public static ContactRequestReservationResult Allowed(int submissionId) => new(true, submissionId, null);

    public static ContactRequestReservationResult Blocked(string errorMessage) => new(false, null, errorMessage);
}