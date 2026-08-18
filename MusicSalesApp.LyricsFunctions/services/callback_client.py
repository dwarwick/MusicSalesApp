"""Posting results and progress back to the web app.

Mirrors ``MusicSalesApp.Functions/Services/MediaProcessingCallbackClient.cs``, and in particular
mirrors **the one behaviour in it that is load-bearing**:

* **Terminal callbacks raise on a non-2xx.**  The orchestration's retry policy is what stops an
  attempt being lost because the site happened to be restarting.
* **Progress callbacks swallow everything.**  A failed cosmetic update must never cost an alignment
  run that has already spent minutes of CPU.

Getting these the same way round is the difference between "the site was briefly down" and "the
creator's song silently never got its lyrics".
"""

from __future__ import annotations

import logging
from typing import Any

import requests

from lyrics.constants import Routes

from .options import FunctionOptions

_logger = logging.getLogger(__name__)

#: What the site is given to assemble a result. Much shorter than the audio pipeline's five minutes,
#: because the work is much smaller - two artifacts of a few tens of kilobytes and one row, against
#: a 150 MB MP3. Mirrors LyricsProcessingTimeouts.TerminalCallback.
_TERMINAL_TIMEOUT_SECONDS = 120

#: Short on purpose: progress is decoration, and a site that has stopped answering must not cost an
#: alignment several minutes per step. Mirrors MediaProcessingTimeouts.ProgressCallback.
_PROGRESS_TIMEOUT_SECONDS = 15


class CallbackClient:
    def __init__(self, options: FunctionOptions) -> None:
        self._base_url = options.callback_base_url
        self._headers = {
            Routes.API_KEY_HEADER: options.media_processing_api_key,
            "Accept": "application/json",
        }

    def post_result(self, result: dict[str, Any]) -> None:
        """The terminal callback. Raises on anything but a 2xx.

        Raising is the point: it is what makes the orchestration's retry policy meaningful. Swallow
        this and a site that was restarting for thirty seconds costs the creator a whole alignment
        run with no trace anywhere.
        """
        url = f"{self._base_url}/{Routes.LYRICS_COMPLETE}"

        response = requests.post(
            url, json=result, headers=self._headers, timeout=_TERMINAL_TIMEOUT_SECONDS
        )

        if not response.ok:
            body = (response.text or "")[:500]
            raise RuntimeError(
                f"Lyrics completion callback for job {result.get('jobId')} failed: "
                f"{response.status_code} {body}"
            )

    def post_progress(self, progress: dict[str, Any]) -> None:
        """A progress ping. Never raises, whatever happens."""
        url = f"{self._base_url}/{Routes.LYRICS_PROGRESS}"

        try:
            requests.post(
                url, json=progress, headers=self._headers, timeout=_PROGRESS_TIMEOUT_SECONDS
            )
        except Exception as exc:  # noqa: BLE001 - swallowed on purpose, see the module docstring.
            _logger.debug(
                "Could not post lyrics progress for job %s at step %s: %s",
                progress.get("jobId"),
                progress.get("step"),
                exc,
            )
