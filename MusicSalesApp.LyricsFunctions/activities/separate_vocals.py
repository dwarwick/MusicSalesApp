"""Isolating the vocal stem with Demucs.

**The single biggest accuracy lever in the pipeline, and the most expensive step by a wide margin.**
Forced alignment against a full mix is markedly worse than against an isolated vocal - drums and bass
give an acoustic model plenty to mistake for consonants - so this is not a stage to skip to save
time, even though skipping it would save most of the runtime.

**Why the track is separated in pieces.**  Demucs' peak memory is driven by the length of the whole
track, not by ``--segment``: it allocates full-length tensors for all four sources and accumulates
the overlap-add result across them, so a long track costs more even though it is processed a segment
at a time.  Flex Consumption caps an instance at 4096 MB - 512, 2048 and 4096 are the only values the
platform accepts - so there is no larger box to move to.  Measured on a 4 GB instance: a 29 s clip
separates comfortably, and the 3 min 41 s track it was cut from is OOM-killed after about four
minutes, with SIGKILL and no Python traceback.  Halving ``--segment`` from 7 to 5 changed nothing,
which is what identified length rather than segment as the driver.

Splitting bounds memory by the chunk instead, and the seams are handled by separating a margin either
side of each chunk and then discarding it - the model gets context around every boundary, and only
the interior it was most confident about is kept.  Crossfading was the alternative and is worse: it
blends two estimates of the same audio, where discarding simply never uses the weaker one.
"""

from __future__ import annotations

import logging
import os
import subprocess
import sys

_logger = logging.getLogger(__name__)


def separate(
    model: str,
    segment: int,
    wav_path: str,
    output_dir: str,
    ffmpeg: str,
    duration_ms: int,
    chunk_seconds: int,
    margin_seconds: int,
) -> str:
    """Run Demucs over the track and return the path to a full-length vocal stem.

    Short tracks take the whole-file path, because chunking one buys nothing and the extra ffmpeg
    passes are pure cost.  ``duration_ms`` of 0 means ffprobe could not read a duration, in which case
    the whole-file path is also the only safe choice - chunking needs a length to divide.
    """
    duration_seconds = duration_ms / 1000.0 if duration_ms else 0.0
    os.makedirs(output_dir, exist_ok=True)

    if duration_seconds <= 0 or duration_seconds <= chunk_seconds + margin_seconds:
        _logger.info("Separating %.1fs in one pass.", duration_seconds)
        return _run_demucs(model, segment, wav_path, os.path.join(output_dir, "whole"))

    pieces: list[str] = []
    core_start = 0.0
    index = 0

    while core_start < duration_seconds:
        core_end = min(core_start + chunk_seconds, duration_seconds)
        extended_start = max(0.0, core_start - margin_seconds)
        extended_end = min(duration_seconds, core_end + margin_seconds)

        chunk_wav = os.path.join(output_dir, f"chunk-{index:03d}.wav")
        _cut(ffmpeg, wav_path, extended_start, extended_end - extended_start, chunk_wav)

        stem = _run_demucs(model, segment, chunk_wav, os.path.join(output_dir, f"stem-{index:03d}"))

        piece = os.path.join(output_dir, f"vocal-{index:03d}.wav")
        _cut(ffmpeg, stem, core_start - extended_start, core_end - core_start, piece)
        pieces.append(piece)

        # Deleted as we go, not at the end. A 4-minute track at 44.1 kHz stereo is ~40 MB per copy,
        # and with the chunk, its stem and the trimmed piece all alive there are three per iteration.
        # Flex gives the instance a small local disk, and filling it fails the run just as hard as
        # running out of memory did.
        for spent in (chunk_wav, stem):
            try:
                os.remove(spent)
            except OSError:
                _logger.warning("Could not remove the intermediate file '%s'.", spent)

        _logger.info(
            "Separated %.1fs-%.1fs (chunk %d, %.1fs of context either side).",
            core_start, core_end, index, margin_seconds,
        )

        core_start = core_end
        index += 1

    return _concatenate(ffmpeg, pieces, os.path.join(output_dir, "vocals.wav"), duration_ms)


def _run_demucs(model: str, segment: int, wav_path: str, output_dir: str) -> str:
    """One Demucs invocation, over whatever audio it is handed.

    Invoked as a subprocess rather than through the Python API deliberately: Demucs allocates large
    tensors, and a subprocess hands every byte of that back to the OS when it exits.  In-process, a
    Function host that keeps the worker alive between invocations would accumulate it across runs
    until an instance that had done several songs was killed for memory on one it should have handled
    comfortably.  With chunking that matters more, not less - this now runs several times per track.

    ``--segment`` is bounded from *above* by the model as well as by memory.  The default htdemucs is
    a hybrid transformer, and Demucs refuses to run one past the segment it was trained for - 7.8
    seconds - exiting with a FATAL rather than clamping.  See ``FunctionOptions.demucs_segment``.
    """
    command = [
        sys.executable,
        "-m",
        "demucs.separate",
        "--two-stems",
        "vocals",
        "-n",
        model,
        "--segment",
        str(segment),
        # CPU is not a preference: Flex Consumption has no GPU. Stating it avoids a probe that can
        # find MPS on a developer's Mac and produce timings that do not predict Azure at all.
        "-d",
        "cpu",
        "-o",
        output_dir,
        wav_path,
    ]

    # The subprocess does NOT inherit the worker's import path, and nothing about
    # `sys.executable -m demucs.separate` hints otherwise. On the Functions Python worker
    # sys.executable is the host's interpreter at /usr/local/bin/python, while the app's
    # dependencies live under /home/site/wwwroot/.python_packages - on sys.path for the in-process
    # worker, invisible to anything it spawns. The result is a bare
    # "ModuleNotFoundError: No module named 'demucs'" from a package that is demonstrably installed.
    #
    # Handing over the parent's own sys.path is the fix that cannot drift: it needs no knowledge of
    # where the platform puts packages this year.
    env = dict(os.environ)
    inherited = os.pathsep.join(entry for entry in sys.path if entry)
    env["PYTHONPATH"] = (
        inherited + os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else inherited
    )

    completed = subprocess.run(command, capture_output=True, text=True, check=False, env=env)

    if completed.returncode != 0:
        raise RuntimeError(
            f"Demucs failed (exit {completed.returncode}): {(completed.stderr or '')[-2000:]}"
        )

    stem = _find_vocal_stem(output_dir)
    if stem is None:
        raise RuntimeError("Demucs reported success but produced no vocal stem.")

    return stem


def _cut(ffmpeg: str, source: str, start_seconds: float, length_seconds: float, destination: str) -> None:
    """Extract a span, re-encoding rather than stream-copying.

    ``-ss`` goes AFTER ``-i`` on purpose.  Before it, ffmpeg seeks by jumping, which is fast and
    approximate; after it, ffmpeg decodes and counts, which is slow and exact.  Every span here is
    concatenated back into a single timeline that lyric timestamps are read off, so a boundary that
    lands a few tens of milliseconds out does not blur one word - it shifts every word after it.
    """
    command = [
        ffmpeg,
        "-hide_banner",
        "-nostdin",
        "-y",
        "-i",
        source,
        "-ss",
        f"{start_seconds:.6f}",
        "-t",
        f"{length_seconds:.6f}",
        "-c:a",
        "pcm_s16le",
        destination,
    ]

    completed = subprocess.run(command, capture_output=True, text=True, check=False)

    if completed.returncode != 0:
        raise RuntimeError(
            f"ffmpeg could not cut {start_seconds:.1f}s+{length_seconds:.1f}s "
            f"(exit {completed.returncode}): {(completed.stderr or '')[-1000:]}"
        )


def _concatenate(ffmpeg: str, pieces: list[str], destination: str, expected_ms: int) -> str:
    """Join the kept spans back into one stem, and check the length while we are here.

    The length check is cheap and catches the failure that would otherwise be silent: every piece is
    a valid WAV and the concatenation always succeeds, so a boundary arithmetic error produces a stem
    that is simply the wrong length.  Downstream that is not an error either - it is timings that
    drift further out of step the longer the song goes on, which surfaces as "the karaoke is broken"
    long after anyone would think to look here.
    """
    listing = os.path.join(os.path.dirname(destination), "pieces.txt")
    with open(listing, "w", encoding="utf-8") as handle:
        for piece in pieces:
            # The concat demuxer treats quotes as syntax; the paths here are ours and contain none.
            handle.write(f"file '{piece}'\n")

    command = [
        ffmpeg,
        "-hide_banner",
        "-nostdin",
        "-y",
        "-f",
        "concat",
        "-safe",
        "0",
        "-i",
        listing,
        "-c:a",
        "pcm_s16le",
        destination,
    ]

    completed = subprocess.run(command, capture_output=True, text=True, check=False)

    if completed.returncode != 0:
        raise RuntimeError(
            f"ffmpeg could not join the vocal pieces (exit {completed.returncode}): "
            f"{(completed.stderr or '')[-1000:]}"
        )

    from activities.prep_audio import probe_duration_ms

    actual_ms = probe_duration_ms(ffmpeg, destination)
    drift_ms = abs(actual_ms - expected_ms)

    if expected_ms and drift_ms > 250:
        _logger.warning(
            "The joined vocal stem is %d ms, expected %d ms (%d ms out). Timings after the first "
            "chunk boundary may be shifted.",
            actual_ms, expected_ms, drift_ms,
        )
    else:
        _logger.info("Joined %d pieces into a %d ms vocal stem.", len(pieces), actual_ms)

    return destination


def _find_vocal_stem(output_dir: str) -> str | None:
    """Demucs writes to ``{output_dir}/{model}/{track name}/vocals.wav``.

    Walked rather than reconstructed: the track-name segment is derived from the input filename by
    Demucs's own rules, and reproducing those here would be a second copy of logic that only it
    controls.
    """
    for root, _dirs, files in os.walk(output_dir):
        for name in files:
            if name.startswith("vocals.") and name.endswith((".wav", ".mp3", ".flac")):
                return os.path.join(root, name)
    return None
