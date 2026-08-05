namespace MusicSalesApp.Functions;

/// <summary>
/// Everything this app needs from its Function App settings.
/// </summary>
public sealed class FunctionOptions
{
    /// <summary>
    /// Standard general-purpose account: the queues, and the upload staging container.
    /// Read/write.
    /// </summary>
    public string StagingStorageConnectionString { get; set; }

    /// <summary>
    /// Premium account holding song media. <b>Read-only from here</b> - the web app owns every
    /// write into the catalogue. Used only by the probe path.
    /// </summary>
    public string MediaStorageConnectionString { get; set; }

    public string StagingContainerName { get; set; }

    public string MediaContainerName { get; set; }

    /// <summary>Site root for callbacks - https://davidtest.dev or https://streamtunes.net.</summary>
    public string CallbackBaseUrl { get; set; }

    /// <summary>Shared secret sent as X-Media-Processing-Key on every callback.</summary>
    public string MediaProcessingApiKey { get; set; }
}
