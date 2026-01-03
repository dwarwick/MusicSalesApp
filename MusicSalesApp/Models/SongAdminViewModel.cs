namespace MusicSalesApp.Models;

/// <summary>
/// View model for the admin song management grid
/// </summary>
public class SongAdminViewModel
{
    public string Id { get; set; } = string.Empty; // Unique identifier (blob name)
    public string SongImageUrl { get; set; } = string.Empty;
    public string AlbumCoverImageUrl { get; set; } = string.Empty;
    public bool IsAlbum { get; set; }
    public string AlbumName { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string Mp3FileName { get; set; } = string.Empty;
    public string JpegFileName { get; set; } = string.Empty;
    public decimal? AlbumPrice { get; set; }
    public decimal? SongPrice { get; set; }
    public string Genre { get; set; } = string.Empty;
    public int? TrackNumber { get; set; }
    public double? TrackLength { get; set; }
    public bool DisplayOnHomePage { get; set; }
    public int NumberOfStreams { get; set; }

    // Additional properties for internal use
    public bool HasAlbumCover { get; set; }
    public string AlbumCoverBlobName { get; set; } = string.Empty;

    // Seller-related properties
    public int? SellerId { get; set; }
    public bool IsActive { get; set; } = true;
}
