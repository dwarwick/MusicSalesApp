using Microsoft.Extensions.Logging;

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Utility methods for working with temporary files.
/// </summary>
public static class TempFileHelper
{
    /// <summary>
    /// Attempts to delete a temporary file. Failures are logged but not thrown,
    /// as the OS will clean up temp files eventually.
    /// </summary>
    public static void TryDelete(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete temp file '{TempPath}'. It will be cleaned up by the OS.", path);
        }
    }
}
