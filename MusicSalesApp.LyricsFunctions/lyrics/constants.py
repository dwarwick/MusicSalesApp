"""Values shared with the web app, which owns the originals.

**This file is a hand-maintained copy, and that is a new hazard in this codebase.**  Every other
cross-process constant here is shared by *compilation*: the C# Function app and the web app both
reference ``MusicSalesApp.Common``, so a mismatch cannot happen.  Python cannot, so these are
transcribed - and a transcription that drifts produces no error anywhere, just a callback nobody
routes or a step nobody recognises.

``tests/test_constants_drift.py`` reads the C# source and asserts the two agree.  If you change
anything here, change it there, and vice versa.

Source of truth:
* ``MusicSalesApp.Common/Contracts/LyricsAlignmentStep.cs``
* ``MusicSalesApp.Common/Helpers/LyricsProcessingConstants.cs``
* ``MusicSalesApp.Common/Helpers/MediaProcessingConstants.cs``
"""

from __future__ import annotations

from enum import IntEnum


class LyricsAlignmentStep(IntEnum):
    """Mirrors ``MusicSalesApp.Common.Contracts.LyricsAlignmentStep``.

    The ordinals are the contract, not the names: the web app compares them with ``>`` to decide
    whether a progress ping is an advance, so a value that drifts makes the creator's bar move
    backwards or freeze.
    """

    SUBMITTED = 0
    QUEUED = 10
    PREPARING = 20
    SEPARATING_VOCALS = 30
    ALIGNING = 60
    MAPPING = 80
    WRITING_OUTPUTS = 90
    COPYING = 93
    SAVING = 96
    COMPLETED = 100
    FAILED = 110


# Mirrors LyricsAlignmentProgressCalculator's band table. Duplicated rather than derived because the
# sender computes the overall percent - that is what stops two processes disagreeing about what
# "40%" means when only one of them has the table.
_BANDS: dict[LyricsAlignmentStep, tuple[float, float]] = {
    LyricsAlignmentStep.SUBMITTED: (0.0, 5.0),
    LyricsAlignmentStep.QUEUED: (5.0, 5.0),
    LyricsAlignmentStep.PREPARING: (5.0, 15.0),
    LyricsAlignmentStep.SEPARATING_VOCALS: (15.0, 70.0),
    LyricsAlignmentStep.ALIGNING: (70.0, 85.0),
    LyricsAlignmentStep.MAPPING: (85.0, 90.0),
    LyricsAlignmentStep.WRITING_OUTPUTS: (90.0, 93.0),
    LyricsAlignmentStep.COPYING: (93.0, 96.0),
    LyricsAlignmentStep.SAVING: (96.0, 100.0),
    LyricsAlignmentStep.COMPLETED: (100.0, 100.0),
    LyricsAlignmentStep.FAILED: (0.0, 100.0),
}


def to_overall_percent(step: LyricsAlignmentStep, step_percent: float | None = None) -> float:
    """The overall 0-100 figure for a step, optionally advanced through its own band."""
    band = _BANDS.get(step)
    if band is None:
        return 0.0

    start, end = band
    if step_percent is None:
        return start

    clamped = max(0.0, min(100.0, step_percent))
    return start + ((end - start) * (clamped / 100.0))


class LyricsAlignmentOutcome:
    """Mirrors ``MusicSalesApp.Common.Contracts.LyricsAlignmentOutcome``.

    Serialised by name rather than by ordinal, because the web app's JSON options accept either and
    a name that drifts fails loudly where an ordinal would silently mean something else.
    """

    ALIGNED = "Aligned"
    UNUSABLE = "Unusable"
    INCONCLUSIVE = "Inconclusive"


class LyricsAlignmentFailureCodes:
    """Mirrors ``MusicSalesApp.Common.Helpers.LyricsAlignmentFailureCodes``."""

    LYRICS_TEXT_EMPTY = "LyricsTextEmpty"
    LYRICS_TEXT_TOO_LONG = "LyricsTextTooLong"
    AUDIO_BLOB_MISSING = "AudioBlobMissing"
    LYRICS_BLOB_MISSING = "LyricsBlobMissing"
    PREPARATION_FAILED = "PreparationFailed"
    SEPARATION_FAILED = "SeparationFailed"
    ALIGNMENT_FAILED = "AlignmentFailed"
    NO_TOKENS_MATCHED = "NoTokensMatched"
    TIMINGS_NOT_MONOTONIC = "TimingsNotMonotonic"
    TIMINGS_EXCEED_DURATION = "TimingsExceedDuration"
    ORCHESTRATION_FAILED = "OrchestrationFailed"
    ORCHESTRATION_TERMINATED = "OrchestrationTerminated"
    STARTER_UNREACHABLE = "StarterUnreachable"
    ABANDONED = "Abandoned"


class Routes:
    """Mirrors ``MediaProcessingRoutes`` - the web app's callback surface."""

    LYRICS_COMPLETE = "api/media-processing/lyrics-complete"
    LYRICS_PROGRESS = "api/media-processing/lyrics-progress"

    #: Deliberately not ``X-Api-Key``: that header is the mobile app's, and its key ships inside a
    #: distributed binary. This one authorises writes to the song catalogue.
    API_KEY_HEADER = "X-Media-Processing-Key"


class StagingPaths:
    """Mirrors ``MediaProcessingStagingPaths``' lyrics members.

    The ``lyrics/`` prefix is load-bearing: the web app's upload cleanup deletes staged blobs by
    ``{guid}/`` prefix, and a lyrics attempt has its own GUID.  Without the prefix the two namespaces
    overlap and an upload tidying up after itself could delete an alignment's output.
    """

    PREFIX = "lyrics"

    @staticmethod
    def folder(job_id: str) -> str:
        return f"{StagingPaths.PREFIX}/{_guid_n(job_id)}"

    @staticmethod
    def timings(job_id: str) -> str:
        return f"{StagingPaths.folder(job_id)}/timings.json"

    @staticmethod
    def lrc(job_id: str) -> str:
        return f"{StagingPaths.folder(job_id)}/lyrics.lrc"


def _guid_n(job_id: str) -> str:
    """The "N" GUID format the whole codebase uses: 32 lowercase hex digits, no hyphens."""
    return job_id.replace("-", "").lower()


#: The name recorded on the web app's DurableFunctionTask row. Must match
#: ``LyricsAlignmentInvoker.OrchestratorName``.
ORCHESTRATOR_NAME = "align_lyrics_orchestrator"
