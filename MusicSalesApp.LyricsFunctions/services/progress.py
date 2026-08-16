"""Reporting progress from inside an activity.

Two decisions worth knowing about, both borrowed from ``ProgressReporter.cs`` and one of them
specific to this pipeline.

**Progress is posted from inside the activities, never from the orchestrator.**  An orchestrator that
called a ``post_progress`` activity would turn every cosmetic ping into a durable history entry and
a handful of storage transactions - and at the volumes involved those can rival the compute bill.

**Vocal separation heartbeats.**  Demucs offers no progress callback at all, and it is by far the
longest stage: single-digit minutes for a normal track, tens of minutes for a long one on a cold
instance.  Without a heartbeat the web app's liveness timestamp would freeze for the whole run and
the reconciler would start interrogating a perfectly healthy orchestration.  The interval matches the
web app's own ``LivenessRefreshInterval``, so a non-advancing ping costs about one row update a
minute - which is the throttle that endpoint was designed around.
"""

from __future__ import annotations

import threading
import time
from typing import Any

from lyrics.constants import LyricsAlignmentStep, to_overall_percent

from .callback_client import CallbackClient

#: Matches MediaProcessingController.LivenessRefreshInterval.
HEARTBEAT_SECONDS = 60


class ProgressReporter:
    """One per activity invocation."""

    def __init__(self, client: CallbackClient, job_id: str) -> None:
        self._client = client
        self._job_id = job_id

    def report(
        self,
        step: LyricsAlignmentStep,
        detail: str | None = None,
        step_percent: float | None = None,
    ) -> None:
        payload: dict[str, Any] = {
            "jobId": self._job_id,
            "step": int(step),
            "stepPercent": step_percent,
            # Computed here rather than at the receiver, so every hop agrees on one number instead of
            # each recomputing it from its own copy of the band table.
            "overallPercent": to_overall_percent(step, step_percent),
            "detail": detail,
        }
        self._client.post_progress(payload)

    def heartbeat(self, step: LyricsAlignmentStep, estimated_seconds: float, detail: str | None = None):
        """A context manager that pings while a long, silent stage runs.

        The percentage is an estimate against an expected duration and will be wrong - it is capped
        just short of the band end so the bar creeps rather than sitting at 100% of a stage that has
        not finished. Being approximately right beats being motionless.
        """
        return _Heartbeat(self, step, estimated_seconds, detail)


class _Heartbeat:
    def __init__(
        self,
        reporter: ProgressReporter,
        step: LyricsAlignmentStep,
        estimated_seconds: float,
        detail: str | None,
    ) -> None:
        self._reporter = reporter
        self._step = step
        self._estimated = max(1.0, estimated_seconds)
        self._detail = detail
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def __enter__(self) -> "_Heartbeat":
        self._reporter.report(self._step, self._detail, step_percent=0.0)
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *_exc_info) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=5)

    def _run(self) -> None:
        started = time.monotonic()

        while not self._stop.wait(HEARTBEAT_SECONDS):
            elapsed = time.monotonic() - started
            # Capped at 95% of the stage: the estimate is a guess, and a bar that sits at 100% of a
            # stage still running reads as stuck rather than nearly done.
            percent = min(95.0, (elapsed / self._estimated) * 100.0)
            self._reporter.report(self._step, self._detail, step_percent=percent)
