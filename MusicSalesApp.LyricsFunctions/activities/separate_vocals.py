"""Isolating the vocal stem with Demucs.

**The single biggest accuracy lever in the pipeline, and the most expensive step by a wide margin.**
Forced alignment against a full mix is markedly worse than against an isolated vocal - drums and bass
give an acoustic model plenty to mistake for consonants - so this is not a stage to skip to save
time, even though skipping it would save most of the runtime.

Expect single-digit minutes for a normal track on the two cores a 4 GB Flex instance gets, and tens
of minutes for a long one on a cold instance.  Nothing here reports progress, which is why the caller
wraps it in a heartbeat.
"""

from __future__ import annotations

import logging
import os
import subprocess
import sys

_logger = logging.getLogger(__name__)


def separate(model: str, segment: int, wav_path: str, output_dir: str) -> str:
    """Run Demucs and return the path to the vocal stem.

    Invoked as a subprocess rather than through the Python API deliberately: Demucs allocates large
    tensors, and a subprocess hands every byte of that back to the OS when it exits.  In-process, a
    Function host that keeps the worker alive between invocations would accumulate it across runs
    until an instance that had done several songs was killed for memory on one it should have handled
    comfortably.

    ``--segment`` is passed explicitly because peak memory scales with it.  At 4 GB an unbounded
    segment on a long track is not obviously safe, and an out-of-memory kill is a hard failure - the
    orchestration does not retry this step, because a retry at the same instance size would fail the
    same way after spending the same minutes.
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

    _logger.info("Separating vocals with %s (segment=%ds).", model, segment)

    completed = subprocess.run(command, capture_output=True, text=True, check=False)

    if completed.returncode != 0:
        raise RuntimeError(
            f"Demucs failed (exit {completed.returncode}): {(completed.stderr or '')[-2000:]}"
        )

    stem = _find_vocal_stem(output_dir)
    if stem is None:
        raise RuntimeError("Demucs reported success but produced no vocal stem.")

    _logger.info("Vocal stem written to %s.", stem)
    return stem


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
