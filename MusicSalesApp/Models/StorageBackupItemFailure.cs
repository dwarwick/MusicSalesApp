using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

/// <summary>
/// A single blob that could not be copied. Only failures are recorded — a per-blob row for every
/// blob would add millions of rows a year across nightly runs for no diagnostic benefit.
/// </summary>
public class StorageBackupItemFailure
{
    [Key]
    public int Id { get; set; }

    public int RunId { get; set; }
    public StorageBackupRun Run { get; set; }

    [MaxLength(128)] public string ContainerName { get; set; }
    [MaxLength(1024)] public string BlobName { get; set; }
    [MaxLength(200)] public string FailureCode { get; set; }
    [MaxLength(2000)] public string Diagnostic { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
