using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing music genres.
/// </summary>
public interface IGenreService
{
    /// <summary>
    /// Gets all active genres ordered alphabetically.
    /// </summary>
    Task<List<Genre>> GetActiveGenresAsync();

    /// <summary>
    /// Gets all genres (including inactive) ordered alphabetically.
    /// </summary>
    Task<List<Genre>> GetAllGenresAsync();

    /// <summary>
    /// Adds a new genre. Returns the created genre, or null if a genre with the same name already exists.
    /// </summary>
    /// <param name="name">The genre name.</param>
    /// <param name="createdByEmail">The email of the user creating the genre.</param>
    Task<Genre> AddGenreAsync(string name, string createdByEmail);

    /// <summary>
    /// Disables a genre by ID. Returns true if successful.
    /// </summary>
    Task<bool> DisableGenreAsync(int genreId);

    /// <summary>
    /// Enables a previously disabled genre by ID. Returns true if successful.
    /// </summary>
    Task<bool> EnableGenreAsync(int genreId);

    /// <summary>
    /// Gets a genre by its ID.
    /// </summary>
    Task<Genre> GetByIdAsync(int genreId);

    /// <summary>
    /// Gets a genre by its name (case-insensitive).
    /// </summary>
    Task<Genre> GetByNameAsync(string name);
}
