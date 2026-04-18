using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface IReportedSongService
{
    Task<ReportedSong> ReportSongAsync(int reportingUserId, int songMetadataId, string reason);
    Task<List<ReportedSong>> GetAllReportsAsync();
    Task<bool> ResolveReportAsync(int reportId, bool accepted);
}
