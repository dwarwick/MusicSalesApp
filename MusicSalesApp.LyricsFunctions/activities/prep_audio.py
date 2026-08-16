"""Turning a playback MP3 into something the rest of the pipeline can work with.

Three outputs, all from ffmpeg: a 16 kHz mono WAV (what every aligner wants), the track's duration,
and a map of the stretches with no sound in them.

The silence map is the one that is easy to skip and shouldn't be.  It becomes a **hard constraint**
in the mapping stage - no lyric word may be placed inside an instrumental break - and it kills a
whole class of drift where the aligner loses the thread over a solo and smears the following words
backwards across it.
"""

from __future__ import annotations

import json
import logging
import re
import subprocess
from dataclasses import dataclass

_logger = logging.getLogger(__name__)

#: ffmpeg prints silencedetect results to stderr, one line per boundary.
_SILENCE_START = re.compile(r"silence_start:\s*([\d.]+)")
_SILENCE_END = re.compile(r"silence_end:\s*([\d.]+)")

#: Below this, and for at least this long, counts as "nobody is singing". Deliberately conservative:
#: a false positive removes a legitimate placement, which is worse than missing a short gap.
_SILENCE_THRESHOLD_DB = "-35dB"
_SILENCE_MIN_SECONDS = "2.0"


@dataclass
class PreparedAudio:
    wav_path: str
    duration_ms: int
    silences: list[dict[str, int]]


def prepare(ffmpeg: str, source_path: str, wav_path: str) -> PreparedAudio:
    """Resample, normalise loudness, and detect silence in a single pass.

    One pass rather than three: the file is decoded once and the silence filter observes the same
    stream that is being written out, so the windows line up with the samples the aligner will see
    rather than with the original's.
    """
    command = [
        ffmpeg,
        "-hide_banner",
        "-nostdin",
        "-y",
        "-i",
        source_path,
        "-af",
        # loudnorm first so silencedetect's threshold means the same thing across quiet and loud
        # masters, then the detector, which passes audio through untouched.
        f"loudnorm=I=-16:TP=-1.5:LRA=11,silencedetect=noise={_SILENCE_THRESHOLD_DB}:d={_SILENCE_MIN_SECONDS}",
        "-ac",
        "1",
        "-ar",
        "16000",
        "-c:a",
        "pcm_s16le",
        wav_path,
    ]

    completed = subprocess.run(command, capture_output=True, text=True, check=False)

    if completed.returncode != 0:
        raise RuntimeError(
            f"ffmpeg could not prepare the audio (exit {completed.returncode}): "
            f"{(completed.stderr or '')[-2000:]}"
        )

    duration_ms = probe_duration_ms(ffmpeg, wav_path)
    silences = _parse_silences(completed.stderr or "", duration_ms)

    _logger.info("Prepared audio: %d ms, %d silent stretches.", duration_ms, len(silences))
    return PreparedAudio(wav_path=wav_path, duration_ms=duration_ms, silences=silences)


def probe_duration_ms(ffmpeg: str, path: str) -> int:
    """Duration in milliseconds, via ffprobe alongside the ffmpeg binary.

    Zero rather than an exception when it cannot be read: a duration this pipeline does not have
    simply disables the clamp and the overshoot check downstream, which is a degraded result rather
    than a failed one.
    """
    ffprobe = ffmpeg.replace("ffmpeg", "ffprobe")

    completed = subprocess.run(
        [
            ffprobe,
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "json",
            path,
        ],
        capture_output=True,
        text=True,
        check=False,
    )

    if completed.returncode != 0:
        _logger.warning("ffprobe could not read a duration from '%s'.", path)
        return 0

    try:
        payload = json.loads(completed.stdout or "{}")
        return int(float(payload["format"]["duration"]) * 1000)
    except (KeyError, ValueError, TypeError):
        _logger.warning("ffprobe returned no usable duration for '%s'.", path)
        return 0


def _parse_silences(stderr: str, duration_ms: int) -> list[dict[str, int]]:
    """Pair up silencedetect's start and end lines.

    A start with no matching end means the track fades out into silence, so the final window runs to
    the end of the track. Left unpaired it would be dropped, and the trailing instrumental would stop
    being a constraint exactly where drift is most likely.
    """
    starts = [float(match) for match in _SILENCE_START.findall(stderr)]
    ends = [float(match) for match in _SILENCE_END.findall(stderr)]

    windows: list[dict[str, int]] = []

    for index, start in enumerate(starts):
        end = ends[index] if index < len(ends) else (duration_ms / 1000.0 if duration_ms else None)
        if end is None or end <= start:
            continue

        windows.append({"startMs": int(start * 1000), "endMs": int(end * 1000)})

    return windows
