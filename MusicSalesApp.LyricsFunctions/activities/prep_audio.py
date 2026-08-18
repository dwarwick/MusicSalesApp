"""Turning a playback MP3 into something the rest of the pipeline can work with.

**Two conversions, not one, because the two consumers want opposite things.**  Demucs was trained on
44.1 kHz stereo; the aligner wants 16 kHz mono.  These were originally a single pass producing the
aligner's format, which was then handed to Demucs as well - so separation, described in its own
module as the single biggest accuracy lever in the pipeline, ran on a 16 kHz mono downmix that it
had to resample back up to 44.1 kHz stereo before it could do anything with it.  Nothing failed; the
stem was just quietly worse than it needed to be, which is the kind of bug that never announces
itself.

The silence map is easy to skip and shouldn't be.  It becomes a **hard constraint** in the mapping
stage - no lyric word may be placed inside an instrumental break - and it kills a whole class of
drift where the aligner loses the thread over a solo and smears the following words backwards across
it.

**It is measured on the vocal stem, not on the mix, and that is what makes it work at all.**  An
instrumental break is not silent in the mix - that is what makes it instrumental rather than a gap -
so detecting silence there finds the count-in and the fade-out and essentially nothing else, leaving
the constraint switched on and inert.  On the isolated vocal, every stretch where nobody is singing
registers, which is exactly the set of windows the mapping stage is trying to keep words out of.
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


def decode_for_separation(ffmpeg: str, source_path: str, wav_path: str) -> int:
    """Decode the playback MP3 to Demucs' native format, and return the duration in milliseconds.

    44.1 kHz stereo because that is what the model was trained on.  Handing it anything else does not
    fail - Demucs resamples and duplicates the channel quite happily - it just separates a signal
    that no longer resembles its training distribution, and the loss lands on the vocal stem the
    whole rest of the pipeline depends on.

    Deliberately unfiltered.  loudnorm belongs on the aligner's copy, where a consistent level makes
    the silence threshold mean the same thing across quiet and loud masters.  Applying it here would
    change the mix Demucs sees for no benefit.
    """
    command = [
        ffmpeg,
        "-hide_banner",
        "-nostdin",
        "-y",
        "-i",
        source_path,
        "-ac",
        "2",
        "-ar",
        "44100",
        "-c:a",
        "pcm_s16le",
        wav_path,
    ]

    completed = subprocess.run(command, capture_output=True, text=True, check=False)

    if completed.returncode != 0:
        raise RuntimeError(
            f"ffmpeg could not decode the audio (exit {completed.returncode}): "
            f"{(completed.stderr or '')[-2000:]}"
        )

    duration_ms = probe_duration_ms(ffmpeg, wav_path)
    _logger.info("Decoded %d ms of 44.1 kHz stereo for separation.", duration_ms)
    return duration_ms


def prepare_for_alignment(ffmpeg: str, vocal_path: str, wav_path: str) -> PreparedAudio:
    """Convert the vocal stem to the aligner's format and map its silences in one pass.

    One pass rather than two: the file is decoded once and the silence filter observes the same
    stream that is being written out, so the windows line up with the samples the aligner will
    actually see rather than with some earlier version of the audio.

    ``duration_ms`` here is the stem's, which is the mix's - separation preserves length - so it
    remains the right value to clamp lyric timings against.
    """
    command = [
        ffmpeg,
        "-hide_banner",
        "-nostdin",
        "-y",
        "-i",
        vocal_path,
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
            f"ffmpeg could not prepare the vocal for alignment (exit {completed.returncode}): "
            f"{(completed.stderr or '')[-2000:]}"
        )

    duration_ms = probe_duration_ms(ffmpeg, wav_path)
    silences = _parse_silences(completed.stderr or "", duration_ms)

    _logger.info("Prepared vocal: %d ms, %d silent stretches.", duration_ms, len(silences))
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
