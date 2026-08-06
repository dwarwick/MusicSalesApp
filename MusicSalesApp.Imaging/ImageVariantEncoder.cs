#nullable enable
using SkiaSharp;

namespace MusicSalesApp.Imaging;

/// <summary>
/// The pixel work behind the image renditions: choosing which widths to produce for a given source,
/// and resampling and encoding one of them.
///
/// <para>
/// Deliberately free of Azure and Entity Framework so the sizing and quality rules can be tested
/// directly. The I/O around it belongs to whoever owns the storage account:
/// <c>ImageVariantService</c> in the web app, <c>CoverArtRenditionGenerator</c> in the Azure
/// Function. Both call this, so a cover art's rendition ladder has the same shape whether it was
/// built at upload or rebuilt by an admin re-crop.
/// </para>
/// </summary>
public static class ImageVariantEncoder
{
    /// <summary>
    /// WebP quality per rendition width.
    ///
    /// Quality descends as the rendition grows: a large rendition is displayed at fewer
    /// pixels-per-unit-of-detail than a thumbnail of the same image, so compression artefacts are
    /// correspondingly less visible — and the large rungs are where nearly all the storage goes.
    /// </summary>
    public static int QualityFor(int width) => width switch
    {
        <= 128 => 82,
        <= 320 => 80,
        <= 640 => 78,
        _ => 75
    };

    /// <summary>
    /// The widths worth generating for a source of <paramref name="sourceWidth"/> pixels.
    ///
    /// <para>
    /// Never upscales: a rendition wider than the source would cost storage and bandwidth to
    /// deliver blur. When the source is narrower than even the smallest rung — a handful of very
    /// old covers are — the result is a single rendition at the source's own width, which is a
    /// straight re-encode into WebP with no resampling. That keeps the invariant the mobile client
    /// depends on: any image with cover art has at least one rendition.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> SelectWidths(IReadOnlyList<int> ladder, int sourceWidth)
    {
        if (ladder == null || ladder.Count == 0 || sourceWidth <= 0)
            return Array.Empty<int>();

        var selected = new List<int>();
        foreach (var width in ladder)
        {
            if (width > 0 && width <= sourceWidth)
                selected.Add(width);
        }

        return selected.Count > 0 ? selected : new[] { sourceWidth };
    }

    /// <summary>
    /// Resamples <paramref name="source"/> to <paramref name="targetWidth"/> and encodes it as
    /// lossy WebP. Aspect ratio is preserved, so a square source — which is what the site enforces
    /// for display — yields a square rendition.
    ///
    /// <para>
    /// Every rendition is drawn from the one decoded source rather than from the next rendition up.
    /// Chaining resamples would compound the interpolation error and visibly soften the smallest
    /// rungs, which are the ones shown most often.
    /// </para>
    /// </summary>
    /// <returns>A rewound stream, or <see langword="null"/> if the encode failed.</returns>
    public static MemoryStream? EncodeWebp(SKBitmap source, int targetWidth)
    {
        if (source == null || source.Width <= 0 || source.Height <= 0 || targetWidth <= 0)
            return null;

        var targetHeight = (int)Math.Round(source.Height * (double)targetWidth / source.Width);
        if (targetHeight < 1)
            targetHeight = 1;

        using var surface = SKSurface.Create(new SKImageInfo(targetWidth, targetHeight));
        if (surface == null)
            return null;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var sourceRect = new SKRect(0, 0, source.Width, source.Height);
        var destRect = new SKRect(0, 0, targetWidth, targetHeight);

        // Mitchell matches the resampler already used for the Open Graph share image, and is the
        // usual choice for photographic downscaling: sharper than a plain triangle filter without
        // the ringing a Lanczos kernel puts on hard edges, which album art has plenty of.
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

        using (var image = SKImage.FromBitmap(source))
        {
            if (image == null)
                return null;

            canvas.DrawImage(image, sourceRect, destRect, sampling);
        }

        using var snapshot = surface.Snapshot();
        if (snapshot == null)
            return null;

        // Lossy WebP. Lossless would be larger than the JPEG or PNG master for photographic art,
        // which defeats the entire point of generating renditions.
        using var data = snapshot.Encode(SKEncodedImageFormat.Webp, QualityFor(targetWidth));
        if (data == null || data.Size == 0)
            return null;

        var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;
        return stream;
    }
}
