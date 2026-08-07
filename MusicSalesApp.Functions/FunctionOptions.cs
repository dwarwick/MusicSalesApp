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
    /// Premium account holding song media. Read by the probe path, and written by the cover-art
    /// rendition pass — but <b>for derived artefacts only</b>. The web app remains the sole writer
    /// of every primary blob and the sole writer of the database; nothing here may create, overwrite
    /// or delete a blob a <c>SongMetadata</c> row already points at.
    /// </summary>
    public string MediaStorageConnectionString { get; set; }

    public string StagingContainerName { get; set; }

    public string MediaContainerName { get; set; }

    /// <summary>Site root for callbacks - https://davidtest.dev or https://streamtunes.net.</summary>
    public string CallbackBaseUrl { get; set; }

    /// <summary>Shared secret sent as X-Media-Processing-Key on every callback.</summary>
    public string MediaProcessingApiKey { get; set; }
}
