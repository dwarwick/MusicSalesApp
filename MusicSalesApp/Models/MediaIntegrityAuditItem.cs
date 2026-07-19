using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

public class MediaIntegrityAuditItem
{
    [Key]
    public int Id { get; set; }
    public int AuditRunId { get; set; }
    public MediaIntegrityAuditRun AuditRun { get; set; }
    public int? SongMetadataId { get; set; }
    public SongMetadata SongMetadata { get; set; }
    [MaxLength(200)] public string EffectiveTitle { get; set; }
    [MaxLength(500)] public string PlaybackBlobPath { get; set; }
    [MaxLength(500)] public string OriginalAudioBlobPath { get; set; }
    public bool IsOriginalSourceMissing { get; set; }
    public bool IsOriginalSourceCheckInconclusive { get; set; }
    public long? BlobLength { get; set; }
    [MaxLength(200)] public string ETag { get; set; }
    [MaxLength(100)] public string ContentType { get; set; }
    public DateTimeOffset? BlobLastModified { get; set; }
    [MaxLength(20)] public string DetectedFormat { get; set; }
    public double? DecodedDuration { get; set; }
    public int Attempts { get; set; }
    public MediaAuditOutcome Outcome { get; set; }
    [MaxLength(100)] public string FailureCode { get; set; }
    [MaxLength(2000)] public string Diagnostic { get; set; }
    public bool MetadataRepaired { get; set; }
    public bool Quarantined { get; set; }
    public bool CreatorNotificationSent { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}
