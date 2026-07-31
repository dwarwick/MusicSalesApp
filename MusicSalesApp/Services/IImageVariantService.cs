#nullable enable

namespace MusicSalesApp.Services;

/// <summary>The outcome of generating renditions for one image.</summary>
/// <param name="Succeeded">Whether renditions were produced (or, for a dry run, could have been).</param>
/// <param name="GeneratedWidths">The widths written, ascending. Empty on failure.</param>
/// <param name="SourceWidth">The master's pixel width, 0 when it could not be decoded.</param>
/// <param name="SourceHeight">The master's pixel height, 0 when it could not be decoded.</param>
/// <param name="BytesWritten">Total encoded bytes across every rendition. Populated on a dry run too, which is what makes the dry run's storage estimate honest.</param>
/// <param name="FailureReason">A short code from <see cref="Common.Helpers.ImageVariantFailureCodes"/>, or null on success.</param>
public sealed record ImageVariantResult(
    bool Succeeded,
    IReadOnlyList<int> GeneratedWidths,
    int SourceWidth,
    int SourceHeight,
    long BytesWritten,
    string? FailureReason)
{
    /// <summary>A failed result carrying only a reason.</summary>
    public static ImageVariantResult Failed(string reason)
        => new(false, Array.Empty<int>(), 0, 0, 0, reason);
}

/// <summary>
/// Generates the pre-resized WebP renditions of cover art and persona images, and removes them.
///
/// <para>
/// Works purely in terms of blob paths. Recording which renditions now exist is
/// <see cref="IImageVariantCoordinator"/>'s job, so this service can be driven equally by an upload
/// and by the admin backfill.
/// </para>
/// </summary>
public interface IImageVariantService
{
    /// <summary>
    /// Regenerates every rendition of a cover-art blob, overwriting unconditionally.
    ///
    /// <para>
    /// There is no "already exists" short circuit on purpose. Cover art under the GUID naming
    /// scheme lives at a fixed path that a re-crop overwrites in place, so the rendition paths do
    /// not change either — a get-or-create would serve the pre-crop framing forever. This is the
    /// same trap <see cref="IOpenGraphService.RefreshSharingImageAsync"/> exists to work around for
    /// the share image.
    /// </para>
    /// </summary>
    /// <param name="dryRun">Decode, resample and encode as normal but write nothing, so a backfill can measure before committing.</param>
    Task<ImageVariantResult> GenerateCoverArtVariantsAsync(
        string coverArtBlobPath,
        bool dryRun = false,
        CancellationToken cancellationToken = default);

    /// <summary>As <see cref="GenerateCoverArtVariantsAsync"/>, against the persona image container.</summary>
    Task<ImageVariantResult> GeneratePersonaVariantsAsync(
        string personaBlobPath,
        bool dryRun = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the named renditions of a cover-art blob. Used when art moves to a different path,
    /// which only happens under the legacy naming scheme; best effort, never throws.
    /// </summary>
    Task DeleteCoverArtVariantsAsync(
        string coverArtBlobPath,
        IEnumerable<int> widths,
        CancellationToken cancellationToken = default);

    /// <summary>As <see cref="DeleteCoverArtVariantsAsync"/>, against the persona image container.</summary>
    Task DeletePersonaVariantsAsync(
        string personaBlobPath,
        IEnumerable<int> widths,
        CancellationToken cancellationToken = default);
}
