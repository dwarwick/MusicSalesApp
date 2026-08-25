using Microsoft.Extensions.Configuration;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// Builds the configuration <see cref="SongLikeService"/> reads for the stream-before-rating rule.
///
/// Fixtures that are about like/dislike mechanics rather than about the rule itself turn it off, so
/// their arrange steps stay about the thing under test. The rule has its own fixture,
/// <see cref="SongLikeServiceStreamRequirementTests"/>, which covers both settings and the default.
/// </summary>
internal static class SongLikeServiceConfiguration
{
    public static IConfiguration RequireStream(bool required) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                [SongLikeService.RequireStreamBeforeRatingKey] = required ? "true" : "false"
            })
            .Build();

    /// <summary>Configuration with the key absent, so the service falls back to its default.</summary>
    public static IConfiguration Empty() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>())
            .Build();
}
