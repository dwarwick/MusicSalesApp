using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

/// <summary>Which kind of image a backfill failure refers to.</summary>
public enum ImageVariantBackfillItemKind
{
    CoverArt = 0,
    PersonaImage = 1
}

/// <summary>
/// One image the backfill could not process, recorded so a partly-failed run can be triaged from
/// the admin page without digging through the application log.
/// </summary>
public class ImageVariantBackfillItemFailure
{
    [Key]
    public int Id { get; set; }

    public int RunId { get; set; }
    public ImageVariantBackfillRun Run { get; set; }

    public ImageVariantBackfillItemKind ItemKind { get; set; }

    /// <summary>The SongMetadata or CreatorPersona id, so the row can be found again.</summary>
    public int EntityId { get; set; }

    [MaxLength(500)] public string BlobPath { get; set; }

    /// <summary>A code from <see cref="Common.Helpers.ImageVariantFailureCodes"/>.</summary>
    [MaxLength(50)] public string FailureCode { get; set; }

    [MaxLength(1000)] public string Message { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
