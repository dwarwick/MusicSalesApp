"""The two things that decide what a creator is told when a run dies.

Both were found by a real failure rather than by reasoning, and both are invisible until an
alignment fails in Azure - which is the worst place to discover either.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

import function_app as fa
from lyrics.constants import LyricsAlignmentFailureCodes, LyricsAlignmentOutcome

_REPO_ROOT = Path(__file__).resolve().parents[2]


def rewrapped(error: Exception) -> Exception:
    """What the orchestrator actually catches.

    Durable does not hand an activity's exception object to the orchestrator.  It serialises the
    failure and raises a *new* exception carrying the original's text, so every attribute - including
    ``LyricsPipelineError.code`` - is gone by the time the except block runs.  Reproduced here rather
    than mocked, because the whole point is that the object identity is lost.
    """
    return Exception(f"Activity function 'align_lyrics' failed:  {type(error).__name__}: {error}")


class TestFailureCodeSurvivesTheActivityBoundary:
    """Without this the creator sees one generic message for every possible cause."""

    def test_the_code_is_recovered_after_durable_rewraps_the_exception(self):
        # The original bug: getattr(error, "code") returned None across the boundary, so a Demucs
        # crash reached the web app as OrchestrationFailed and the creator was told nothing useful.
        original = fa.LyricsPipelineError(
            LyricsAlignmentFailureCodes.SEPARATION_FAILED, "Demucs failed (exit 1): ..."
        )

        assert fa._recover_failure_code(rewrapped(original)) == "SeparationFailed"

    def test_the_code_is_still_read_from_the_attribute_when_it_is_present(self):
        # In-process - no boundary crossed - the attribute is authoritative and must not be
        # second-guessed by parsing text.
        original = fa.LyricsPipelineError(
            LyricsAlignmentFailureCodes.NO_TOKENS_MATCHED, "nothing matched"
        )

        assert fa._recover_failure_code(original) == "NoTokensMatched"

    @pytest.mark.parametrize(
        "code,expected",
        [
            (LyricsAlignmentFailureCodes.NO_TOKENS_MATCHED, LyricsAlignmentOutcome.UNUSABLE),
            (LyricsAlignmentFailureCodes.AUDIO_BLOB_MISSING, LyricsAlignmentOutcome.UNUSABLE),
            (LyricsAlignmentFailureCodes.SEPARATION_FAILED, LyricsAlignmentOutcome.INCONCLUSIVE),
            (LyricsAlignmentFailureCodes.ALIGNMENT_FAILED, LyricsAlignmentOutcome.INCONCLUSIVE),
        ],
    )
    def test_the_outcome_split_survives_the_boundary_too(self, code, expected):
        # Unusable tells the creator to fix their submission; Inconclusive offers a Re-run. Getting
        # this backwards either nags about lyrics that were fine or hides a transient failure.
        assert fa._outcome_for(rewrapped(fa.LyricsPipelineError(code, "boom"))) == expected

    def test_a_code_name_appearing_as_free_text_is_not_mistaken_for_the_code(self):
        # Diagnostics quote Demucs stderr and Python tracebacks verbatim, so the text is attacker-ish
        # in the sense that it can contain anything. Matching is bracketed for exactly this reason.
        noise = Exception("traceback mentions AlignmentFailed somewhere in a stack frame")

        assert fa._recover_failure_code(noise) == "OrchestrationFailed"

    def test_an_unrecognised_failure_falls_back_rather_than_guessing(self):
        assert fa._recover_failure_code(Exception("segmentation fault")) == "OrchestrationFailed"


class TestDemucsSegmentStaysUnderTheTransformerCap:
    """htdemucs refuses a segment longer than it was trained for, and it is a FATAL, not a clamp."""

    #: Demucs' own message: "Maximum segment is: 7.8". Any value above this fails 100% of the time.
    TRANSFORMER_MAX_SEGMENT = 7.8

    def test_the_python_default_is_within_the_cap(self):
        # This was 8 - two tenths of a second over - and it failed every single run, after paying for
        # the model download first, which made a config typo look like an infrastructure problem.
        default = int(
            re.search(r'_optional\("DEMUCS_SEGMENT",\s*"(\d+)"\)',
                      (Path(fa.__file__).parent / "services" / "options.py").read_text()).group(1)
        )

        assert default < self.TRANSFORMER_MAX_SEGMENT

    def test_the_provisioned_value_matches_the_python_default(self):
        # The two live in files that never mention each other - a PowerShell hashtable and a Python
        # dataclass - so nothing but this test connects them.
        script = _REPO_ROOT / "Get-LyricsProcessingSettings.ps1"
        if not script.exists():
            pytest.skip(f"{script} is not available in this checkout.")

        provisioned = int(
            re.search(r'"DEMUCS_SEGMENT"\s*=\s*"(\d+)"', script.read_text(encoding="utf-8-sig")).group(1)
        )

        assert provisioned < self.TRANSFORMER_MAX_SEGMENT
        assert provisioned == int(
            re.search(r'_optional\("DEMUCS_SEGMENT",\s*"(\d+)"\)',
                      (Path(fa.__file__).parent / "services" / "options.py").read_text()).group(1)
        )
