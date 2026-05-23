namespace MusicSalesApp.Services;

/// <summary>
/// Repairs missing track lengths for active playable songs.
/// </summary>
public interface ITrackLengthRepairService
{
    /// <summary>
    /// Recomputes track lengths for active songs whose metadata is currently missing a duration.
    /// </summary>
    /// <returns>The number of songs successfully updated.</returns>
    Task<int> RepairMissingTrackLengthsAsync();
}