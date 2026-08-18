"""Serialising timings: JSON for the player, Enhanced LRC for export.

The JSON is the primary artifact and the only one the player reads.  The LRC costs almost nothing to
produce alongside it and buys portability for free - it is what every other karaoke tool in the world
understands, and it is the format an artist would hand back if they wanted to correct the timings by
hand.
"""

from __future__ import annotations

from typing import Any

from .align_map import AlignmentOutput, TimedLine


def to_timing_json(song_metadata_id: int, output: AlignmentOutput) -> dict[str, Any]:
    """The player-facing document.

    Lines with no timing are included rather than dropped.  A blank line or a ``[Chorus]`` marker is
    part of how the artist laid the song out, and a display that silently removed them would not be
    showing the lyrics that were submitted.  They simply carry nulls, and the player skips them.
    """
    return {
        "songId": song_metadata_id,
        "durationMs": output.stats.duration_ms,
        "confidence": round(output.stats.confidence, 4),
        "lines": [_line_to_json(line) for line in output.lines],
    }


def _line_to_json(line: TimedLine) -> dict[str, Any]:
    return {
        "text": line.text,
        "startMs": line.start_ms,
        "endMs": line.end_ms,
        "words": [
            {
                "text": word.text,
                "startMs": word.start_ms,
                "endMs": word.end_ms,
            }
            for word in line.words
        ],
    }


def to_enhanced_lrc(output: AlignmentOutput, title: str | None = None, artist: str | None = None) -> str:
    """Enhanced LRC: a line timestamp, then an inline timestamp before each word.

    Untimed lines are emitted without a timestamp rather than omitted, for the same reason the JSON
    keeps them - and because a reader that hits an untimed line simply shows it, which is the
    behaviour wanted for a section heading.
    """
    lines: list[str] = []

    if title:
        lines.append(f"[ti:{title}]")
    if artist:
        lines.append(f"[ar:{artist}]")

    for line in output.lines:
        if line.start_ms is None:
            lines.append(line.text)
            continue

        parts = [f"[{_format_timestamp(line.start_ms)}]"]
        for word in line.words:
            if word.start_ms is None:
                parts.append(f" {word.text}")
            else:
                parts.append(f"<{_format_timestamp(word.start_ms)}>{word.text} ")

        lines.append("".join(parts).rstrip())

    return "\n".join(lines) + "\n"


def _format_timestamp(ms: int) -> str:
    """``mm:ss.xx`` - the LRC convention, hundredths rather than milliseconds.

    Minutes are not wrapped at 60: a 75-minute mix is ``75:00.00`` rather than ``15:00.00``, which is
    what every LRC reader expects and the only unambiguous reading.
    """
    ms = max(0, ms)
    total_seconds, milliseconds = divmod(ms, 1000)
    minutes, seconds = divmod(total_seconds, 60)
    hundredths = milliseconds // 10
    return f"{minutes:02d}:{seconds:02d}.{hundredths:02d}"
