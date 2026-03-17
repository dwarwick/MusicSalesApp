#nullable enable

namespace MusicSalesApp.Services;

public interface IMaintenanceResetService
{
    Task ResetExpiredMaintenanceWindowsAsync();
}
