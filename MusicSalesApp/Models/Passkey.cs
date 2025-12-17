using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

public class Passkey
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public ApplicationUser User { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public byte[] CredentialId { get; set; }

    [Required]
    public byte[] PublicKey { get; set; }

    [Required]
    public byte[] AttestationObject { get; set; }

    [Required]
    public byte[] ClientDataJSON { get; set; }

    public int SignCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(200)]
    public string AAGUID { get; set; } = string.Empty;
}
