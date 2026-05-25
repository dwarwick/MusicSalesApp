#nullable enable

using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

public class ContactRequestAdminService : IContactRequestAdminService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public ContactRequestAdminService(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<ContactRequestSubmissionDto>> GetSubmissionsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ContactRequestSubmissions
            .AsNoTracking()
            .OrderByDescending(submission => submission.SubmittedAtUtc)
            .Select(submission => new ContactRequestSubmissionDto
            {
                Id = submission.Id,
                UserId = submission.UserId,
                UserEmail = submission.UserEmail,
                Subject = submission.Subject,
                MessageText = submission.MessageText,
                MessageLength = submission.MessageLength,
                IpAddress = submission.IpAddress,
                SubmittedAtUtc = submission.SubmittedAtUtc,
                UserEmailSent = submission.UserEmailSent,
                AdminEmailSent = submission.AdminEmailSent,
                EmailSendCompletedAtUtc = submission.EmailSendCompletedAtUtc
            })
            .ToListAsync();
    }
}