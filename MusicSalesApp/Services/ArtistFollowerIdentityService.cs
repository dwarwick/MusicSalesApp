namespace MusicSalesApp.Services;

/// <inheritdoc />
public class ArtistFollowerIdentityService : IArtistFollowerIdentityService
{
    /// <summary>
    /// Bands to draw from, tried in order. A band is skipped once it is more than
    /// <see cref="OccupancyCeiling"/> full, so random probing stays cheap - at 30% occupancy the
    /// expected number of probes is under 1.5.
    /// </summary>
    private static readonly (int Inclusive, int Exclusive)[] Bands =
    [
        (1_000, 100_000),
        (100_000, 1_000_000),
        (1_000_000, 10_000_000),
    ];

    private const double OccupancyCeiling = 0.3;
    private const int MaxProbes = 64;

    private readonly Random _random;

    public ArtistFollowerIdentityService()
        : this(Random.Shared)
    {
    }

    /// <summary>
    /// Test seam. Production always uses <see cref="Random.Shared"/>.
    /// </summary>
    public ArtistFollowerIdentityService(Random random)
    {
        _random = random ?? Random.Shared;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Random, not derived from the listener's id.</b> A keyed hash of the user id would give
    /// the same stability, but it stays a deterministic function of the identity it is hiding, so
    /// the day the key leaks every pseudonym in the system resolves at once and every creator can
    /// cross-reference their follower list against every other creator's. Random numbers carry no
    /// such latent link - the only thing that can undo one is the row itself.
    /// </para>
    /// <para>
    /// <b>Random, not sequential.</b> A counter would leak the order people followed in, which
    /// sits next to a "Following Since" column and turns two coarse dates into an ordering of
    /// everyone in between.
    /// </para>
    /// </remarks>
    public int AllocateNumber(IReadOnlySet<int> numbersAlreadyUsedForPersona)
    {
        var used = numbersAlreadyUsedForPersona ?? (IReadOnlySet<int>)new HashSet<int>();

        foreach (var (inclusive, exclusive) in Bands)
        {
            var size = exclusive - inclusive;

            // Count only what falls inside this band; a persona that has outgrown the first band
            // still holds numbers there, and those must not count against the second.
            var occupied = used.Count(number => number >= inclusive && number < exclusive);
            if (occupied >= size * OccupancyCeiling)
            {
                continue;
            }

            for (var probe = 0; probe < MaxProbes; probe++)
            {
                var candidate = _random.Next(inclusive, exclusive);
                if (!used.Contains(candidate))
                {
                    return candidate;
                }
            }

            // Probing was unlucky rather than the band being full. Scanning from a random offset
            // keeps the result unpredictable, where scanning from the start would hand out
            // low numbers in order and reintroduce exactly the sequential leak avoided above.
            var offset = _random.Next(0, size);
            for (var step = 0; step < size; step++)
            {
                var candidate = inclusive + ((offset + step) % size);
                if (!used.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        // Ten million followers of a single persona. Not reachable in practice, and a wrong number
        // here would be silently shared between two listeners, so it throws rather than guessing.
        throw new InvalidOperationException(
            "No anonymous listener number is available for this persona.");
    }

    /// <inheritdoc />
    public string FormatDisplayName(int anonymousListenerNumber) =>
        $"Listener #{anonymousListenerNumber}";
}
