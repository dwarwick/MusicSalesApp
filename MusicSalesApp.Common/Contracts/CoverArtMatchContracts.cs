namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// Asks the Function to pair a batch of staged cover-art candidates with the audio files a creator
/// dropped alongside them.
///
/// <para>
/// This runs <em>before</em> any song exists: pairing is what decides which image belongs to which
/// upload, so it cannot ride along on a per-song job. The batch is identified by its own GUID, and
/// nothing about it is persisted — see <see cref="CreatorId"/>.
/// </para>
/// </summary>
public sealed class CoverArtMatchRequest
{
    public Guid BatchId { get; set; }

    /// <summary>
    /// Who is waiting for the answer.
    ///
    /// <para>
    /// Round-trips through the queue and back on the callbacks because there is deliberately no
    /// database row for a match batch — nothing about it needs to outlive the circuit that started
    /// it, and a lost message degrades to local exact-name matching rather than a lost song. The
    /// receiving controller must use this for the SignalR group name and <b>nothing else</b>.
    /// </para>
    /// </summary>
    public int CreatorId { get; set; }

    /// <summary>
    /// The audio filenames, in the order the page holds them. Names only — the audio itself is never
    /// staged for matching, because only the filename participates.
    /// </summary>
    public IReadOnlyList<string> AudioFileNames { get; set; }

    public IReadOnlyList<CoverArtMatchCandidate> Images { get; set; }
}

/// <summary>One staged image a song might be paired with.</summary>
public sealed class CoverArtMatchCandidate
{
    /// <summary>
    /// Position in the batch. Also names the staged blob, so a creator's filename — which may carry
    /// apostrophes, ampersands or non-ASCII — never reaches a blob path.
    /// </summary>
    public int Index { get; set; }

    /// <summary>The creator's own filename. Travels as data, and is half of what matching compares.</summary>
    public string FileName { get; set; }

    /// <summary>Path within the staging container.</summary>
    public string BlobPath { get; set; }

    public string ContentType { get; set; }
}

/// <summary>
/// The pairing, posted back once. Everything is index-based, so the page maps results onto its own
/// collections and a malformed response can only ever address files the page already had.
/// </summary>
public sealed class CoverArtMatchResult
{
    public Guid BatchId { get; set; }

    public int CreatorId { get; set; }

    public IReadOnlyList<CoverArtMatchPair> Pairs { get; set; }

    public IReadOnlyList<int> UnmatchedImageIndexes { get; set; }

    /// <summary>
    /// True when the vision/pairing model was unavailable and the Function fell back to exact
    /// base-name matching. Not an error: it is the same answer the site produced before OpenAI was
    /// wired in, and the creator can still fix any pairing by hand.
    /// </summary>
    public bool UsedFallback { get; set; }

    public string Diagnostic { get; set; }
}

/// <summary>One audio file and the image it was paired with, if any.</summary>
public sealed class CoverArtMatchPair
{
    public int AudioIndex { get; set; }

    /// <summary>Null when no image was a good enough match for this audio file.</summary>
    public int? ImageIndex { get; set; }

    /// <summary>A cleaned-up song name, used to seed the title the creator then reviews.</summary>
    public string NormalizedName { get; set; }
}

/// <summary>
/// Progress for the pre-pairing phase.
///
/// <para>
/// Deliberately not <see cref="AudioProcessingProgress"/>: there is no job id to carry, and the
/// receiver has no row to compare a step against. Losing one of these costs a slightly jumpier bar
/// and nothing else.
/// </para>
/// </summary>
public sealed class CoverArtMatchProgress
{
    public Guid BatchId { get; set; }

    public int CreatorId { get; set; }

    public CoverArtMatchStep Step { get; set; }

    /// <summary>
    /// Images whose text has been read so far, out of <see cref="ImagesTotal"/>. The only unit in
    /// this phase with an honest denominator, which is why it is carried literally rather than
    /// flattened into a percentage the receiver would have to reverse-engineer for its label.
    /// </summary>
    public int ImagesProcessed { get; set; }

    public int ImagesTotal { get; set; }

    /// <summary>0-100 for the matching phase, computed by <c>CoverArtMatchProgressCalculator</c>.</summary>
    public double OverallPercent { get; set; }

    public string Detail { get; set; }
}
