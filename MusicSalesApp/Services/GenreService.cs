using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing music genres in the database.
/// </summary>
public class GenreService : IGenreService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<GenreService> _logger;

    public GenreService(IDbContextFactory<AppDbContext> contextFactory, ILogger<GenreService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<List<Genre>> GetActiveGenresAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Genres
            .Where(g => g.IsActive)
            .OrderBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<List<Genre>> GetAllGenresAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Genres
            .OrderBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<Genre> AddGenreAsync(string name, string createdByEmail)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmedName = name.Trim();

        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check for duplicate (case-insensitive)
        var existing = await context.Genres
            .FirstOrDefaultAsync(g => g.Name.ToLower() == trimmedName.ToLower());

        if (existing != null)
        {
            _logger.LogWarning("Attempted to add duplicate genre '{GenreName}' by {Email}", trimmedName, createdByEmail);
            return null;
        }

        var genre = new Genre
        {
            Name = trimmedName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedByEmail = createdByEmail ?? string.Empty
        };

        context.Genres.Add(genre);
        await context.SaveChangesAsync();

        _logger.LogInformation("New genre '{GenreName}' added by {Email}", trimmedName, createdByEmail);
        return genre;
    }

    public async Task<bool> DisableGenreAsync(int genreId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var genre = await context.Genres.FindAsync(genreId);

        if (genre == null)
            return false;

        genre.IsActive = false;
        await context.SaveChangesAsync();

        _logger.LogInformation("Genre '{GenreName}' (ID: {GenreId}) has been disabled", genre.Name, genreId);
        return true;
    }

    public async Task<Genre> GetByIdAsync(int genreId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Genres.FindAsync(genreId);
    }

    public async Task<Genre> GetByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Genres
            .FirstOrDefaultAsync(g => g.Name.ToLower() == name.Trim().ToLower());
    }
}
