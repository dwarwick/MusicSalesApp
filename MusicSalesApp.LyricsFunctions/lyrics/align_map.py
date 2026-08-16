"""Mapping an aligner's tokens back onto the artist's own lines and words.

This is where quality is won or lost, and it is pure Python - no model, no I/O, no Azure.  That is
deliberate: it means the part most likely to be wrong is also the part that can be exercised
exhaustively in milliseconds.

The aligner's output will never match the artist's text exactly.  It mishears words, invents
none-existent ones, hears ad-libs the sheet does not contain, and skips words the singer swallowed.
Four things have to be true of the result regardless:

* every word the artist typed appears in the output, timed or not;
* the times never go backwards;
* no word is placed inside a stretch of the song with no singing in it;
* the confidence reported is honest about how much of the above was guessed.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from difflib import SequenceMatcher

from .normalize import LyricLine, LyricToken


@dataclass(frozen=True)
class AlignedToken:
    """One word the forced aligner claims to have found, and where."""

    norm: str
    start_ms: int
    end_ms: int
    score: float = 1.0


@dataclass(frozen=True)
class SilenceWindow:
    """A stretch with no vocal in it, as reported by ffmpeg's ``silencedetect``."""

    start_ms: int
    end_ms: int

    def contains(self, ms: int) -> bool:
        return self.start_ms <= ms < self.end_ms


@dataclass
class TimedWord:
    text: str
    start_ms: int | None
    end_ms: int | None


@dataclass
class TimedLine:
    text: str
    start_ms: int | None
    end_ms: int | None
    words: list[TimedWord] = field(default_factory=list)


@dataclass
class AlignmentStats:
    """Measurements, not verdicts.

    Every field here is a fact about what happened.  Whether the result is good enough to show a
    listener is decided server-side, against an admin-tunable threshold - so this module never
    decides anything is "good", it only reports what it did.
    """

    lyric_token_count: int = 0
    matched_token_count: int = 0
    interpolated_token_count: int = 0
    dropped_aligner_token_count: int = 0
    silence_violation_count: int = 0
    line_count: int = 0
    lines_with_timing_count: int = 0
    is_monotonic: bool = True
    last_word_end_ms: int = 0
    duration_ms: int = 0
    mean_aligner_confidence: float = 0.0
    confidence: float = 0.0


@dataclass
class AlignmentOutput:
    lines: list[TimedLine]
    stats: AlignmentStats


def map_to_lines(
    lines: list[LyricLine],
    tokens: list[LyricToken],
    aligned: list[AlignedToken],
    silences: list[SilenceWindow] | None = None,
    duration_ms: int = 0,
) -> AlignmentOutput:
    """Produce per-line and per-word timings for the artist's lyrics."""
    silences = silences or []
    stats = AlignmentStats(
        lyric_token_count=len(tokens),
        line_count=len(lines),
        duration_ms=duration_ms,
    )

    # start_ms/end_ms per lyric token, None until something fills it in.
    starts: list[int | None] = [None] * len(tokens)
    ends: list[int | None] = [None] * len(tokens)
    matched_scores: list[float] = []

    if tokens and aligned:
        _copy_matched_timings(tokens, aligned, starts, ends, matched_scores, stats)
        _interpolate_gaps(starts, ends, stats)

    _enforce_monotonic(starts, ends, stats, record=True)

    if silences:
        _push_out_of_silence(starts, ends, silences, stats)

        # Nudging words out of a break can reorder them - two words either side of one edge can end
        # up on the same instant, or crossed. Ordering is re-established, but deliberately WITHOUT
        # touching stats.is_monotonic: that field records whether the *aligner* contradicted itself,
        # and the server treats it as a hard structural failure. Blaming it for a correction this
        # module chose to make would fail songs that aligned perfectly well and merely sang over a
        # silence boundary.
        _enforce_monotonic(starts, ends, stats, record=False)

    if duration_ms > 0:
        _clamp_to_duration(starts, ends, duration_ms)

    timed_lines = _roll_up_to_lines(lines, tokens, starts, ends)

    stats.lines_with_timing_count = sum(
        1 for line in timed_lines if line.start_ms is not None and line.end_ms is not None
    )
    stats.last_word_end_ms = max((e for e in ends if e is not None), default=0)
    stats.mean_aligner_confidence = (
        sum(matched_scores) / len(matched_scores) if matched_scores else 0.0
    )
    stats.confidence = _score_confidence(stats)

    return AlignmentOutput(lines=timed_lines, stats=stats)


def _copy_matched_timings(
    tokens: list[LyricToken],
    aligned: list[AlignedToken],
    starts: list[int | None],
    ends: list[int | None],
    matched_scores: list[float],
    stats: AlignmentStats,
) -> None:
    """Align the two token streams and copy timings across for whatever lines up.

    ``SequenceMatcher`` is used rather than a bespoke dynamic-programming aligner, and it earns its
    place for a reason beyond simplicity: its matching blocks are **non-crossing** by construction.
    That is exactly the property needed for repeated choruses - the second chorus can only ever map
    to aligner tokens after the first, never snap back to it, which is the classic failure mode of a
    naive nearest-match approach.

    ``autojunk`` is switched off deliberately.  It treats any element appearing in more than 1% of a
    long sequence as noise, and in a lyric sheet that describes the most common words in the language.
    """
    lyric_norms = [token.norm for token in tokens]
    aligned_norms = [token.norm for token in aligned]

    matcher = SequenceMatcher(None, lyric_norms, aligned_norms, autojunk=False)

    matched_aligner_indices: set[int] = set()

    for lyric_start, aligned_start, length in matcher.get_matching_blocks():
        for offset in range(length):
            lyric_index = lyric_start + offset
            aligned_index = aligned_start + offset

            source = aligned[aligned_index]
            starts[lyric_index] = source.start_ms
            ends[lyric_index] = source.end_ms
            matched_scores.append(source.score)
            matched_aligner_indices.add(aligned_index)

    stats.matched_token_count = len(matched_scores)

    # Aligner tokens nothing in the sheet claims: ad-libs, breaths misheard as words, or the
    # aligner's own errors.  Dropped rather than shown - the artist's text is authoritative about
    # what the words are, and only the aligner is authoritative about when they happen.
    stats.dropped_aligner_token_count = len(aligned) - len(matched_aligner_indices)


def _interpolate_gaps(
    starts: list[int | None],
    ends: list[int | None],
    stats: AlignmentStats,
) -> None:
    """Give unmatched words a time by spreading them evenly between their matched neighbours.

    A word the aligner missed still has to be displayed, and displaying it untimed would leave a hole
    in the middle of a line.  Linear interpolation is crude but honest, and the confidence score
    records how much of the result was arrived at this way.

    Runs at the very start or very end of the song have only one neighbour.  Those are anchored to it
    rather than extrapolated - guessing outward past the last thing actually heard is how words end
    up past the end of the track.
    """
    total = len(starts)
    index = 0
    interpolated = 0

    while index < total:
        if starts[index] is not None:
            index += 1
            continue

        run_start = index
        while index < total and starts[index] is None:
            index += 1
        run_end = index  # exclusive

        before_end = ends[run_start - 1] if run_start > 0 else None
        after_start = starts[run_end] if run_end < total else None

        run_length = run_end - run_start

        if before_end is None and after_start is None:
            # Nothing matched anywhere. Leave the whole run untimed rather than invent a timeline.
            continue

        if before_end is None:
            # Leading run: pin to the first thing actually heard, zero-width.
            for position in range(run_start, run_end):
                starts[position] = after_start
                ends[position] = after_start
                interpolated += 1
            continue

        if after_start is None:
            # Trailing run: pin to the last thing actually heard.
            for position in range(run_start, run_end):
                starts[position] = before_end
                ends[position] = before_end
                interpolated += 1
            continue

        span = max(0, after_start - before_end)
        step = span / (run_length + 1)

        for offset in range(run_length):
            position = run_start + offset
            word_start = int(before_end + (step * offset))
            word_end = int(before_end + (step * (offset + 1)))
            starts[position] = word_start
            ends[position] = max(word_start, word_end)
            interpolated += 1

    stats.interpolated_token_count = interpolated


def _enforce_monotonic(
    starts: list[int | None],
    ends: list[int | None],
    stats: AlignmentStats,
    record: bool,
) -> None:
    """Make sure time only ever moves forward, optionally recording that it had to be corrected.

    A player binary-searches this array to find the active word.  One entry out of order and the
    search can land anywhere, so this is not a cosmetic tidy-up.

    ``record`` says whose fault a correction is.  On the first pass it is the aligner's, and the
    server treats that as a hard structural failure - so it is reported.  On the pass after words
    have been nudged out of instrumental breaks the disorder is this module's own doing, and
    reporting it would fail a song for something the pipeline chose to do to it.
    """
    highest = 0
    corrected = False

    for index, start in enumerate(starts):
        if start is None:
            continue

        if start < highest:
            corrected = True
            starts[index] = highest
            start = highest

        end = ends[index]
        if end is None or end < start:
            ends[index] = start
            end = start

        highest = max(highest, end)

    if record:
        stats.is_monotonic = not corrected


def _push_out_of_silence(
    starts: list[int | None],
    ends: list[int | None],
    silences: list[SilenceWindow],
    stats: AlignmentStats,
) -> None:
    """Keep words out of the stretches with no singing in them.

    This kills a whole class of drift.  When the aligner loses the thread across an instrumental
    break it tends to smear the following words backwards across the solo, and every one of them
    lands somewhere the listener can hear there is nobody singing - which reads as far more broken
    than a word being 200 ms early.

    ``silencedetect`` output is treated as a hard constraint rather than a hint, and each word it
    moves is counted, because a lot of them means the alignment lost the thread rather than merely
    wobbled.
    """
    ordered = sorted(silences, key=lambda window: window.start_ms)
    violations = 0

    for index, start in enumerate(starts):
        if start is None:
            continue

        for window in ordered:
            if not window.contains(start):
                continue

            violations += 1

            width = max(0, (ends[index] or start) - start)

            # Whichever edge is nearer, and note which way each one moves the word. Pulling back
            # means the word belongs to the phrase *before* the break, so it has to END as the break
            # begins - placing its start there would leave it inside the window it was being taken
            # out of. Pushing forward is the mirror image: it starts as the break ends.
            distance_to_start = start - window.start_ms
            distance_to_end = window.end_ms - start

            if distance_to_start <= distance_to_end:
                new_end = window.start_ms
                new_start = max(0, new_end - width)
            else:
                new_start = window.end_ms
                new_end = new_start + width

            starts[index] = new_start
            ends[index] = new_end
            break

    stats.silence_violation_count = violations


def _clamp_to_duration(
    starts: list[int | None],
    ends: list[int | None],
    duration_ms: int,
) -> None:
    """Nothing may be placed past the end of the track."""
    for index in range(len(starts)):
        if starts[index] is not None:
            starts[index] = min(starts[index], duration_ms)  # type: ignore[type-var]
        if ends[index] is not None:
            ends[index] = min(ends[index], duration_ms)  # type: ignore[type-var]
            if starts[index] is not None and ends[index] < starts[index]:  # type: ignore[operator]
                ends[index] = starts[index]


def _roll_up_to_lines(
    lines: list[LyricLine],
    tokens: list[LyricToken],
    starts: list[int | None],
    ends: list[int | None],
) -> list[TimedLine]:
    """Collapse tokens back into the artist's words and lines.

    Contraction expansion means one displayed word can own several tokens - "don't" is matched as
    "do" and "not" - so tokens are grouped by their (line, word) position and the word spans from the
    first token's start to the last one's end.
    """
    timed_lines = [TimedLine(text=line.text, start_ms=None, end_ms=None) for line in lines]

    # (line_index, word_index) -> [text, start, end]
    word_spans: dict[tuple[int, int], TimedWord] = {}
    word_order: list[tuple[int, int]] = []

    for index, token in enumerate(tokens):
        key = (token.line_index, token.word_index)
        start = starts[index]
        end = ends[index]

        existing = word_spans.get(key)
        if existing is None:
            word_spans[key] = TimedWord(text=token.text, start_ms=start, end_ms=end)
            word_order.append(key)
            continue

        if start is not None:
            existing.start_ms = start if existing.start_ms is None else min(existing.start_ms, start)
        if end is not None:
            existing.end_ms = end if existing.end_ms is None else max(existing.end_ms, end)

    for key in word_order:
        line_index, _ = key
        if line_index >= len(timed_lines):
            continue
        timed_lines[line_index].words.append(word_spans[key])

    for line in timed_lines:
        timed = [word for word in line.words if word.start_ms is not None]
        if not timed:
            continue
        line.start_ms = min(word.start_ms for word in timed)  # type: ignore[type-var]
        line.end_ms = max(
            (word.end_ms if word.end_ms is not None else word.start_ms) for word in timed
        )

    return timed_lines


def _score_confidence(stats: AlignmentStats) -> float:
    """One number for "how much of this should be believed".

    Three things drag it down, and they are genuinely different failures:

    * the aligner's own uncertainty, which is what it reports per token;
    * coverage - how much of the artist's text was actually found rather than interpolated between
      neighbours;
    * words that had to be shoved out of an instrumental break, which is evidence the alignment lost
      the thread rather than merely wobbled.

    Coverage is folded in as a multiplier that bottoms out at half rather than at zero, because an
    alignment which found half the words is genuinely worth about half as much - not worthless.  The
    silence penalty is capped so that a handful of moved words cannot sink an otherwise good result.
    """
    if stats.lyric_token_count == 0:
        return 0.0

    coverage = stats.matched_token_count / stats.lyric_token_count
    silence_penalty = min(0.2, stats.silence_violation_count / stats.lyric_token_count)

    score = stats.mean_aligner_confidence * (0.5 + (0.5 * coverage)) - silence_penalty
    return max(0.0, min(1.0, score))
