namespace MusicSalesApp.Models;

/// <summary>
/// View model for the creator persona management grid.
/// </summary>
public class PersonaAdminViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string WebsiteUrl { get; set; } = string.Empty;
    public string ImageBlobPath { get; set; } = string.Empty;

    /// <summary>
    /// The SAS URL for displaying the persona image in the browser.
    /// </summary>
    public string PersonaImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether the persona image is a perfect square.
    /// Null means dimensions are unknown.
    /// </summary>
    public bool? IsImageSquare { get; set; }

    /// <summary>
    /// Number of songs linked to this persona.
    /// </summary>
    public int SongCount { get; set; }

    /// <summary>
    /// Whether this persona is enabled and visible on the site.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Email address of the creator who owns this persona (used in admin view).
    /// </summary>
    public string CreatorEmail { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the creator who owns this persona (used in admin view).
    /// </summary>
    public string CreatorName { get; set; } = string.Empty;

    /// <summary>
    /// The creator's internal ID.
    /// </summary>
    public int CreatorId { get; set; }
}
