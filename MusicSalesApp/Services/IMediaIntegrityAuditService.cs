using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface IMediaIntegrityAuditService
{
    Task<MediaIntegrityAuditRun> StartAsync(
        MediaAuditMode mode,
        int? initiatedByUserId,
        string initiatedByEmail,
        int? sourceRunId = null);

    Task RunAsync(int runId);
    Task<List<MediaIntegrityAuditRun>> GetRunsAsync();
    Task<MediaIntegrityAuditRun> GetRunAsync(int runId);
}
