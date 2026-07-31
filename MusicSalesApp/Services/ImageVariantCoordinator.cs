#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

/// <summary>
/// The single entry point every cover-art and persona-image write path calls after it has committed
/// a new image.
///
/// <para>
/// Regenerating renditions is more than one step — remove whatever was left at the previous path,
/// generate at the new one, then record the width set and bump the cache-busting version — and
/// there are six write paths across the upload flow, the creator pages and the admin pages.
/// Centralising the sequence here keeps them from each re-implementing it slightly differently,
/// which is exactly the problem <see cref="IOpenGraphService.RefreshSharingImageAsync"/> was
/// introduced to solve for the share image.
/// </para>
/// </summary>
public interface IImageVariantCoordinator
{
    /// <summary>
    /// Refreshes the renditions for a song's current cover art.
    ///
    /// <para>
    /// Best effort by design: it logs and returns <see langword="false"/> rather than throwing.
    /// Renditions are derived data that the admin backfill can rebuild at any time, so a SkiaSharp
    /// hiccup must never fail an upload whose real blobs committed successfully. A song with no
    /// recorded widths simply serves its full-size master, which is the behaviour that predates
    /// this feature.
    /// </para>
    /// </summary>
    /// <param name="previousCoverArtBlobPath">
    /// Where the art lived before, when it moved. Only legacy name-based art moves; GUID-scheme art
    /// keeps a fixed path, so this is normally null or equal to the current path.
    /// </param>
    Task<bool> RefreshCoverArtVariantsAsync(
        int songMetadataId,
        string? previousCoverArtBlobPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>As <see cref="RefreshCoverArtVariantsAsync"/>, for a creator persona's image.</summary>
    Task<bool> RefreshPersonaVariantsAsync(
        int personaId,
        string? previousImageBlobPath = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ImageVariantCoordinator : IImageVariantCoordinator
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IImageVariantService _variantService;
    private readonly ILogger<ImageVariantCoordinator> _logger;

    public ImageVariantCoordinator(
        IDbContextFactory<AppDbContext> contextFactory,
        IImageVariantService variantService,
        ILogger<ImageVariantCoordinator> logger)
    {
        _contextFactory = contextFactory;
        _variantService = variantService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> RefreshCoverArtVariantsAsync(
        int songMetadataId,
        string? previousCoverArtBlobPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var song = await context.SongMetadata
                .FirstOrDefaultAsync(s => s.Id == songMetadataId, cancellationToken);

            if (song == null)
            {
                _logger.LogWarning(
                    "Cannot refresh cover-art renditions: song {SongMetadataId} no longer exists", songMetadataId);
                return false;
            }

            await DeleteSupersededAsync(
                previousCoverArtBlobPath,
                song.ImageBlobPath,
                song.CoverArtVariantWidths,
                isPersona: false,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(song.ImageBlobPath))
            {
                // The art was removed rather than replaced. Clear the width set so nothing keeps
                // pointing at renditions of a master that is gone.
                song.CoverArtVariantWidths = string.Empty;
                song.CoverArtVariantVersion++;
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }

            var result = await _variantService.GenerateCoverArtVariantsAsync(
                song.ImageBlobPath, dryRun: false, cancellationToken);

            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Could not generate cover-art renditions for song {SongMetadataId} ({BlobPath}): {Reason}",
                    songMetadataId, song.ImageBlobPath, result.FailureReason);
                return false;
            }

            song.CoverArtVariantWidths = ImageVariantSizes.ToCsv(result.GeneratedWidths);
            song.CoverArtVariantVersion++;
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Generated cover-art renditions {Widths} for song {SongMetadataId}",
                song.CoverArtVariantWidths, songMetadataId);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refreshing cover-art renditions for song {SongMetadataId} failed", songMetadataId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RefreshPersonaVariantsAsync(
        int personaId,
        string? previousImageBlobPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var persona = await context.CreatorPersonas
                .FirstOrDefaultAsync(p => p.Id == personaId, cancellationToken);

            if (persona == null)
            {
                _logger.LogWarning(
                    "Cannot refresh persona renditions: persona {PersonaId} no longer exists", personaId);
                return false;
            }

            await DeleteSupersededAsync(
                previousImageBlobPath,
                persona.ImageBlobPath,
                persona.ImageVariantWidths,
                isPersona: true,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(persona.ImageBlobPath))
            {
                persona.ImageVariantWidths = string.Empty;
                persona.ImageVariantVersion++;
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }

            var result = await _variantService.GeneratePersonaVariantsAsync(
                persona.ImageBlobPath, dryRun: false, cancellationToken);

            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Could not generate persona renditions for persona {PersonaId} ({BlobPath}): {Reason}",
                    personaId, persona.ImageBlobPath, result.FailureReason);
                return false;
            }

            persona.ImageVariantWidths = ImageVariantSizes.ToCsv(result.GeneratedWidths);
            persona.ImageVariantVersion++;
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Generated persona renditions {Widths} for persona {PersonaId}",
                persona.ImageVariantWidths, personaId);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refreshing renditions for persona {PersonaId} failed", personaId);
            return false;
        }
    }

    /// <summary>
    /// Removes renditions stranded at a path the image has moved away from.
    ///
    /// <para>
    /// Only meaningful for legacy name-based art, whose path is derived from the song title or
    /// filename and therefore changes when either does. GUID-scheme art keeps one fixed path, so
    /// the previous and current paths are equal and the regeneration overwrites in place — which is
    /// why generation never short circuits on "already exists".
    /// </para>
    /// </summary>
    private async Task DeleteSupersededAsync(
        string? previousBlobPath,
        string? currentBlobPath,
        string? recordedWidthsCsv,
        bool isPersona,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(previousBlobPath))
            return;

        if (string.Equals(previousBlobPath, currentBlobPath, StringComparison.OrdinalIgnoreCase))
            return;

        // Delete exactly what was recorded for the old master. Sweeping the whole ladder would also
        // try to delete rungs that never existed for a small source — harmless, but it turns one
        // storage round trip per real rendition into one per ladder entry.
        var widths = ImageVariantSizes.ParseCsv(recordedWidthsCsv);
        if (widths.Count == 0)
            return;

        if (isPersona)
            await _variantService.DeletePersonaVariantsAsync(previousBlobPath, widths, cancellationToken);
        else
            await _variantService.DeleteCoverArtVariantsAsync(previousBlobPath, widths, cancellationToken);
    }
}
