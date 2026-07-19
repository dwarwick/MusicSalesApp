using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

public class MediaIntegrityAuditNotification
{
    [Key]
    public int Id { get; set; }
    public int AuditRunId { get; set; }
    public MediaIntegrityAuditRun AuditRun { get; set; }
    [MaxLength(50)] public string NotificationType { get; set; }
    [MaxLength(256)] public string Recipient { get; set; }
    public int Attempts { get; set; }
    public bool Sent { get; set; }
    public DateTime? SentAt { get; set; }
    [MaxLength(2000)] public string LastError { get; set; }
}
