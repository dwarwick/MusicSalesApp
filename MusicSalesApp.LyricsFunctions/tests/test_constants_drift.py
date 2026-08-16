"""Guarding the one thing in this repository that is shared by transcription rather than by compiler.

The C# Function app and the web app both reference ``MusicSalesApp.Common``, so a queue name or a
step ordinal cannot drift between them - the compiler would not allow it.  This app cannot reference
that assembly, so ``lyrics/constants.py`` is a hand copy, and a hand copy that drifts produces no
error anywhere.  A step ordinal one out makes the creator's progress bar run backwards; a route
typo makes every callback a 404 in a log nobody is reading.

So these tests read the C# source directly and assert the two agree.  Crude, and much better than
the alternative, which is finding out in production.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

from lyrics.constants import (
    ORCHESTRATOR_NAME,
    LyricsAlignmentFailureCodes,
    LyricsAlignmentStep,
    Routes,
    StagingPaths,
    to_overall_percent,
)

_REPO_ROOT = Path(__file__).resolve().parents[2]
_COMMON = _REPO_ROOT / "MusicSalesApp.Common"
_WEB = _REPO_ROOT / "MusicSalesApp"


def _read(path: Path) -> str:
    if not path.exists():
        pytest.skip(f"{path} is not available in this checkout.")
    return path.read_text(encoding="utf-8-sig")


class TestStepOrdinals:
    """The ordinals are the contract - the web app compares them with ``>``."""

    def test_every_step_matches_the_csharp_enum(self):
        source = _read(_COMMON / "Contracts" / "LyricsAlignmentStep.cs")

        found = {
            name: int(value)
            for name, value in re.findall(r"^\s*(\w+)\s*=\s*(\d+),?\s*$", source, re.MULTILINE)
        }

        assert found, "Could not parse any members out of LyricsAlignmentStep.cs"

        expected = {
            "Submitted": LyricsAlignmentStep.SUBMITTED,
            "Queued": LyricsAlignmentStep.QUEUED,
            "Preparing": LyricsAlignmentStep.PREPARING,
            "SeparatingVocals": LyricsAlignmentStep.SEPARATING_VOCALS,
            "Aligning": LyricsAlignmentStep.ALIGNING,
            "Mapping": LyricsAlignmentStep.MAPPING,
            "WritingOutputs": LyricsAlignmentStep.WRITING_OUTPUTS,
            "Copying": LyricsAlignmentStep.COPYING,
            "Saving": LyricsAlignmentStep.SAVING,
            "Completed": LyricsAlignmentStep.COMPLETED,
            "Failed": LyricsAlignmentStep.FAILED,
        }

        assert found == {name: int(value) for name, value in expected.items()}

    def test_failed_outranks_every_other_step(self):
        # So a late in-flight ping can never overwrite a recorded failure.
        assert LyricsAlignmentStep.FAILED == max(LyricsAlignmentStep)


class TestProgressBands:
    """The sender computes the overall percent, so both copies of the table have to agree."""

    def test_the_bands_match_the_csharp_calculator(self):
        source = _read(_COMMON / "Contracts" / "LyricsAlignmentProgressCalculator.cs")

        found = {
            name: (float(start), float(end))
            for name, start, end in re.findall(
                r"\[LyricsAlignmentStep\.(\w+)\]\s*=\s*\(([\d.]+)d,\s*([\d.]+)d\)", source
            )
        }

        assert found, "Could not parse the band table out of the C# calculator."

        for csharp_name, (start, end) in found.items():
            python_name = re.sub(r"(?<!^)(?=[A-Z])", "_", csharp_name).upper()
            step = LyricsAlignmentStep[python_name]

            assert to_overall_percent(step) == pytest.approx(start), f"{csharp_name} band start"
            assert to_overall_percent(step, 100.0) == pytest.approx(end), f"{csharp_name} band end"


class TestRoutesAndHeaders:
    def test_the_callback_routes_match(self):
        source = _read(_COMMON / "Helpers" / "MediaProcessingConstants.cs")

        controller = re.search(r'ControllerRoute\s*=\s*"([^"]+)"', source)
        assert controller, "Could not find ControllerRoute."

        for csharp_field, python_value in (
            ("LyricsComplete", Routes.LYRICS_COMPLETE),
            ("LyricsProgress", Routes.LYRICS_PROGRESS),
        ):
            suffix = re.search(rf'{csharp_field}\s*=\s*ControllerRoute\s*\+\s*"([^"]+)"', source)
            assert suffix, f"Could not find {csharp_field}."
            assert python_value == controller.group(1) + suffix.group(1)

    def test_the_api_key_header_matches(self):
        source = _read(_COMMON / "Helpers" / "MediaProcessingConstants.cs")

        match = re.search(r'ApiKeyHeaderName\s*=\s*"([^"]+)"', source)
        assert match
        assert Routes.API_KEY_HEADER == match.group(1)


class TestFailureCodes:
    def test_every_python_code_exists_in_the_csharp_class(self):
        source = _read(_COMMON / "Helpers" / "LyricsProcessingConstants.cs")

        declared = set(re.findall(r'public const string \w+ = "([^"]+)";', source))
        assert declared, "Could not parse any failure codes out of the C# source."

        python_codes = {
            value
            for name, value in vars(LyricsAlignmentFailureCodes).items()
            if not name.startswith("_") and isinstance(value, str)
        }

        missing = python_codes - declared
        assert not missing, f"Codes this app sends that the web app does not know: {sorted(missing)}"


class TestStagingPaths:
    def test_the_lyrics_prefix_matches(self):
        source = _read(_COMMON / "Helpers" / "MediaProcessingConstants.cs")

        match = re.search(r'LyricsPrefix\s*=\s*"([^"]+)"', source)
        assert match
        assert StagingPaths.PREFIX == match.group(1)

    def test_the_output_names_match(self):
        source = _read(_COMMON / "Helpers" / "MediaProcessingConstants.cs")

        timings = re.search(r'LyricsTimingsName\s*=\s*"([^"]+)"', source)
        lrc = re.search(r'LyricsLrcName\s*=\s*"([^"]+)"', source)
        assert timings and lrc

        job_id = "0f9c1d2e-3a4b-4c5d-6e7f-8a9b0c1d2e3f"
        assert StagingPaths.timings(job_id).endswith("/" + timings.group(1))
        assert StagingPaths.lrc(job_id).endswith("/" + lrc.group(1))

    def test_a_staging_folder_cannot_collide_with_an_upload_job_folder(self):
        # The upload pipeline deletes staged blobs by "{guid}/". A lyrics attempt has its own GUID,
        # so without the prefix an upload cleaning up after itself could delete an alignment's output.
        job_id = "0f9c1d2e-3a4b-4c5d-6e7f-8a9b0c1d2e3f"
        folder = StagingPaths.folder(job_id)

        assert folder.startswith("lyrics/")
        assert not folder.startswith(job_id.replace("-", ""))

    def test_guids_are_formatted_without_hyphens(self):
        # "N" format throughout the codebase. With hyphens, the "-lyrics" suffix on media paths would
        # be ambiguous and the path parser unreliable.
        folder = StagingPaths.folder("0F9C1D2E-3A4B-4C5D-6E7F-8A9B0C1D2E3F")
        assert folder == "lyrics/0f9c1d2e3a4b4c5d6e7f8a9b0c1d2e3f"


class TestOrchestratorName:
    def test_it_matches_what_the_web_app_records(self):
        source = _read(_WEB / "Services" / "LyricsAlignmentInvoker.cs")

        match = re.search(r'OrchestratorName\s*=\s*"([^"]+)"', source)
        assert match
        assert ORCHESTRATOR_NAME == match.group(1)
