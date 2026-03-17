#nullable enable
using Microsoft.JSInterop;

namespace MusicSalesApp.Helpers;

public static class TimeZoneHelper
{
    public static async Task<MaintenanceLocalTimeInfo> GetUserLocalTimeAsync(
        IJSRuntime js, DateTime? startUtc, DateTime? endUtc)
    {
        try
        {
            return await js.InvokeAsync<MaintenanceLocalTimeInfo>("getMaintenanceLocalTime",
                startUtc?.ToString("O"), endUtc?.ToString("O"));
        }
        catch
        {
            // Fallback to UTC display
            return new MaintenanceLocalTimeInfo
            {
                StartLocal = startUtc?.ToString("g") ?? "",
                EndLocal = endUtc?.ToString("g") ?? "",
                TimeZoneAbbreviation = "UTC"
            };
        }
    }
}

public class MaintenanceLocalTimeInfo
{
    public string StartLocal { get; set; } = string.Empty;
    public string EndLocal { get; set; } = string.Empty;
    public string TimeZoneAbbreviation { get; set; } = string.Empty;
}
