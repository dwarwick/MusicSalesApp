using MusicSalesApp.Functions.Services;
using SkiaSharp;

namespace MusicSalesApp.Tests.Functions;

/// <summary>
/// An in-memory <see cref="IMediaBlobStore"/> for the image tests.
///
/// <para>
/// Records what was written into the media container and lets a test choose how staging reads and
/// media writes fail, which is the only way to reach the failure model that matters: every image
/// fault must still let the song publish.
/// </para>
/// </summary>
public sealed class FakeMediaBlobStore : IMediaBlobStore
{
    /// <summary>Media blob path → the bytes written there. Later writes overwrite, as Azure does.</summary>
    public Dictionary<string, byte[]> MediaWrites { get; } = new(StringComparer.Ordinal);

    /// <summary>Staging blob path → the bytes a download produces.</summary>
    public Dictionary<string, byte[]> StagedBlobs { get; } = new(StringComparer.Ordinal);

    /// <summary>Thrown by the next staging download when set.</summary>
    public Exception DownloadFailure { get; set; }

    /// <summary>Thrown by every media write at or after this one-based index when set.</summary>
    public int? FailMediaWriteFromIndex { get; set; }

    public Task DownloadStagedAsync(string blobPath, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (DownloadFailure is not null)
            throw DownloadFailure;

        if (!StagedBlobs.TryGetValue(blobPath, out var bytes))
            throw new Azure.RequestFailedException(404, "Not found");

        File.WriteAllBytes(destinationPath, bytes);
        return Task.CompletedTask;
    }

    public Task UploadMediaAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (FailMediaWriteFromIndex is { } failFrom && MediaWrites.Count + 1 >= failFrom)
            throw new InvalidOperationException("Simulated media write failure.");

        using var buffer = new MemoryStream();
        content.Position = 0;
        content.CopyTo(buffer);
        MediaWrites[blobPath] = buffer.ToArray();
        return Task.CompletedTask;
    }

    public Task UploadStagedAsync(string blobPath, string sourcePath, string contentType, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> TryDownloadMediaAsync(string blobPath, string destinationPath, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<MediaBlobProperties> GetMediaPropertiesAsync(string blobPath, CancellationToken cancellationToken = default)
        => Task.FromResult(new MediaBlobProperties(false, null, null, null, null));

    /// <summary>
    /// A real encoded PNG, so SkiaSharp decodes it for real rather than against a stub. Carries some
    /// structure because a flat colour compresses to almost nothing and makes size assertions moot.
    /// </summary>
    public static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = SKColors.Orange, IsAntialias = true };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
