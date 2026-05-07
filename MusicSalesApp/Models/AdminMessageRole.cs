using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

public class AdminMessageRole
{
    [Key]
    public int Id { get; set; }

    public int AdminMessageId { get; set; }

    public virtual AdminMessage AdminMessage { get; set; } = default!;

    [Required]
    [MaxLength(256)]
    public string RoleName { get; set; } = string.Empty;
}