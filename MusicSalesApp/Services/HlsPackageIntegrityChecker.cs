#nullable enable
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

/// <summary>
/// Confirms that every song claiming an encrypted-HLS package actually has one in storage.
///
/// <para>
/// This exists because the database and the blob containers can disagree silently, and the
/// disagreement is invisible from either side alone. Restore the blobs without the streaming
/// container and every row still carries its <c>HlsStreamId</c>, so the catalogue looks perfectly
/// healthy while nothing plays. The manifest endpoint does answer 503 per song, but only once a
/// listener asks — so without this the first report of a half-restored container is a complaint.
/// </para>
///
/// <para>
/// Deliberately separate from the probe pipeline in <see cref="MediaIntegrityAuditService"/>. That
/// audit asks "can FFmpeg decode this MP3", which needs a Function and arrives as a callback minutes
/// later. This asks "does the blob exist", which is a HEAD request and needs nothing.
/// </para>
/// </summary>
public interface IHlsPackageIntegrityChecker
{
    Task<HlsPackageIntegrityReport> CheckAsync(CancellationToken cancellationToken = default);
}

/// <param name="SongMetadataId">The song whose package is wrong.</param>
/// <param name="HlsStreamId">The package the row points at.</param>
/// <param name="Problem">What is wrong with it, in words an operator can act on.</param>
public sealed record HlsPackageProblem(int SongMetadataId, Guid HlsStreamId, string Problem);

/// <summary>The outcome of one sweep.</summary>
public sealed class HlsPackageIntegrityReport
{
    /// <summary>
    /// Cap on listed problems. A wiped container makes every song a problem, and the useful signal
    /// there is "everything is gone" — which the count already conveys, and a row per song would not
    /// add to. Matches the bound the packaging backfill puts on its own failure rows.
    /// </summary>
    public const int MaxReportedProblems = 200;

    /// <summary>How many songs were examined.</summary>
    public int CheckedCount { get; init; }

    /// <summary>Songs whose package is missing or incomplete, capped at <see cref="MaxReportedProblems"/>.</summary>
    public IReadOnlyList<HlsPackageProblem> Problems { get; init; } = Array.Empty<HlsPackageProblem>();

    /// <summary>How many problems were found in total, even when not all of them were listed.</summary>
    public int ProblemCount { get; init; }

    /// <summary>
    /// True when storage could not be reached, so the sweep proves nothing.
    ///
    /// <para>
    /// Kept separate from <see cref="ProblemCount"/> rather than folded into it: "we could not ask"
    /// and "the packages are gone" call for opposite responses, and a storage blip must never be
    /// able to look like a catalogue-wide outage that wants a repackage.
    /// </para>
    /// </summary>
    public bool WasInconclusive { get; init; }
}

/// <inheritdoc />
public sealed class HlsPackageIntegrityChecker : IHlsPackageIntegrityChecker
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IBlobContainerFactory _containerFactory;
    private readonly ILogger<HlsPackageIntegrityChecker> _logger;

    public HlsPackageIntegrityChecker(
        IDbContextFactory<AppDbContext> contextFactory,
        IBlobContainerFactory containerFactory,
        ILogger<HlsPackageIntegrityChecker> logger)
    {
        _contextFactory = contextFactory;
        _containerFactory = containerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HlsPackageIntegrityReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var songs = await context.SongMetadata
            .AsNoTracking()
            .Where(song => song.IsActive && song.IsEnabled && song.HlsStreamId != null)
            .Select(song => new SongPackage(song.Id, song.HlsStreamId!.Value, song.HlsSegmentCount))
            .ToListAsync(cancellationToken);

        var container = _containerFactory.GetStreamingContainer();
        var problems = new List<HlsPackageProblem>();
        var problemCount = 0;
        var examined = 0;

        foreach (var song in songs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? problem;
            try
            {
                problem = await FindProblemAsync(container, song, cancellationToken);
            }
            catch (Exception ex)
            {
                // One unreachable storage account would make every remaining answer wrong in the same
                // direction, so the sweep stops rather than condemning the whole catalogue. Abandoning
                // it is the conservative outcome - a genuine gap is still there for the next run.
                _logger.LogWarning(
                    ex,
                    "The HLS package check could not reach storage; abandoning the sweep after {Checked} song(s).",
                    examined);

                return new HlsPackageIntegrityReport
                {
                    CheckedCount = examined,
                    Problems = problems,
                    ProblemCount = problemCount,
                    WasInconclusive = true
                };
            }

            examined++;

            if (problem == null)
            {
                continue;
            }

            problemCount++;

            if (problems.Count < HlsPackageIntegrityReport.MaxReportedProblems)
            {
                problems.Add(new HlsPackageProblem(song.SongMetadataId, song.HlsStreamId, problem));
            }
        }

        if (problemCount > 0)
        {
            // Warning, not Information: this is the state where the database looks healthy and the
            // catalogue does not play, and it is fixed by running the repair backfill rather than by
            // waiting for it to settle.
            _logger.LogWarning(
                "{ProblemCount} of {Checked} songs record an encrypted-HLS package that storage does "
                + "not hold, or does not hold completely. Run the HLS packaging backfill with scope "
                + "RepairMissing. First affected song ids: {SongIds}",
                problemCount,
                examined,
                string.Join(", ", problems.Take(20).Select(problem => problem.SongMetadataId)));
        }

        return new HlsPackageIntegrityReport
        {
            CheckedCount = examined,
            Problems = problems,
            ProblemCount = problemCount
        };
    }

    /// <summary>
    /// Returns what is wrong with one package, or null when it is intact.
    ///
    /// <para>
    /// Two HEAD requests rather than a listing or a manifest download. The manifest proves the
    /// package exists at all; the <em>last</em> segment proves it is complete, because a partial
    /// restore or an interrupted upload leaves a prefix of the segments behind and any earlier
    /// segment would still be present. The last one is therefore the cheapest question that tells
    /// "intact" apart from "half here".
    /// </para>
    /// </summary>
    private static async Task<string?> FindProblemAsync(
        BlobContainerClient container,
        SongPackage song,
        CancellationToken cancellationToken)
    {
        var manifest = container.GetBlobClient(HlsPackagePaths.Manifest(song.HlsStreamId));
        if (!await manifest.ExistsAsync(cancellationToken))
        {
            return "the package folder holds no manifest";
        }

        if (song.SegmentCount is not > 0)
        {
            // Packaged before the segment count was recorded, or recorded as zero. The manifest being
            // present is all this can honestly assert about it.
            return null;
        }

        var lastIndex = song.SegmentCount.Value - 1;
        var lastSegment = container.GetBlobClient(HlsPackagePaths.Segment(song.HlsStreamId, lastIndex));

        return await lastSegment.ExistsAsync(cancellationToken)
            ? null
            : $"the manifest is present but segment {lastIndex} of {song.SegmentCount.Value} is missing";
    }

    private sealed record SongPackage(int SongMetadataId, Guid HlsStreamId, int? SegmentCount);
}
