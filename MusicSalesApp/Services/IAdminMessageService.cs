#nullable enable

namespace MusicSalesApp.Services;

public interface IAdminMessageService
{
    Task<IReadOnlyList<string>> GetAvailableRoleNamesAsync();

    Task<AdminMessageSummaryDto> CreateMessageAsync(CreateAdminMessageRequest request, int createdByUserId);

    Task<IReadOnlyList<AdminMessageSummaryDto>> GetMessagesAsync();

    Task<IReadOnlyList<PendingAdminMessageDto>> GetPendingDialogMessagesAsync(int userId);

    Task<bool> AcknowledgeMessageAsync(int userId, int messageId);

    Task<int> CancelMessageAsync(int messageId, int canceledByUserId);

    Task<AdminMessageEmailJobResult> SendPendingEmailsAsync();
}

public sealed class CreateAdminMessageRequest
{
    public string Subject { get; set; } = string.Empty;

    public string MessageText { get; set; } = string.Empty;

    public IReadOnlyCollection<string> RoleNames { get; set; } = [];

    public bool SendEmail { get; set; }

    public bool ShowDialog { get; set; }
}

public sealed class AdminMessageSummaryDto
{
    public int Id { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string MessageText { get; set; } = string.Empty;

    public IReadOnlyList<string> RoleNames { get; set; } = [];

    public bool SendEmail { get; set; }

    public bool ShowDialog { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? CanceledAtUtc { get; set; }

    public int RecipientCount { get; set; }

    public int AcknowledgedCount { get; set; }

    public int PendingCount { get; set; }

    public int EmailedCount { get; set; }

    public int CanceledCount { get; set; }

    public string RolesDisplay => string.Join(", ", RoleNames);

    public string ChannelsDisplay => (ShowDialog, SendEmail) switch
    {
        (true, true) => "Dialogue, Email",
        (true, false) => "Dialogue",
        (false, true) => "Email",
        _ => "None"
    };
}

public sealed class PendingAdminMessageDto
{
    public int MessageId { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string MessageText { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

public sealed class AdminMessageEmailJobResult
{
    public int ConsideredCount { get; set; }

    public int SentCount { get; set; }

    public int FailedCount { get; set; }

    public int SkippedCount { get; set; }
}