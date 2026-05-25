#nullable enable

namespace MusicSalesApp.Services;

public interface IContactRequestAdminService
{
    Task<IReadOnlyList<ContactRequestSubmissionDto>> GetSubmissionsAsync();
}

public sealed class ContactRequestSubmissionDto
{
    private const int PreviewLength = 140;

    public int Id { get; set; }

    public int UserId { get; set; }

    public string UserEmail { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string MessageText { get; set; } = string.Empty;

    public int MessageLength { get; set; }

    public string? IpAddress { get; set; }

    public DateTime SubmittedAtUtc { get; set; }

    public bool UserEmailSent { get; set; }

    public bool AdminEmailSent { get; set; }

    public DateTime? EmailSendCompletedAtUtc { get; set; }

    public string MessagePreview => CreatePreview(MessageText);

    public string EmailStatus => (UserEmailSent, AdminEmailSent) switch
    {
        (true, true) => "Both sent",
        (true, false) => "User sent",
        (false, true) => "Admin sent",
        _ => "Not sent"
    };

    private static string CreatePreview(string messageText)
    {
        var normalized = string.Join(" ", messageText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= PreviewLength ? normalized : normalized[..PreviewLength] + "...";
    }
}