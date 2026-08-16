"""Settings, read once at import.

Mirrors ``MusicSalesApp.Functions/FunctionOptions.cs``, including its naming split: connection
strings and callback settings are top level, container names live under ``MediaProcessing:``.  The
names match deliberately so ``Get-MediaProcessingSettings.ps1`` can serve both Function apps without
maintaining two vocabularies for the same values.
"""

from __future__ import annotations

import os
from dataclasses import dataclass


def _required(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        # Fail loudly at startup rather than at the first blob read. A Function app missing a
        # connection string is misconfigured, not unlucky, and finding that out on the first upload
        # of the day is worse than finding it out on deploy.
        raise RuntimeError(
            f"Required app setting '{name}' is missing. "
            "Generate local.settings.json with Sync-FunctionSettings.ps1, or check the app's "
            "Application Settings in the portal."
        )
    return value


def _optional(name: str, default: str = "") -> str:
    return os.environ.get(name) or default


@dataclass(frozen=True)
class FunctionOptions:
    staging_connection_string: str
    media_connection_string: str
    staging_container: str
    media_container: str
    callback_base_url: str
    media_processing_api_key: str

    #: Where the Linux ffmpeg binary lives. On Flex Consumption this is an Azure Files mount rather
    #: than something in the deployment package - the package is zip-deployed and already close to
    #: the size limit with PyTorch in it.
    ffmpeg_binary: str

    #: Demucs model to use. htdemucs is the default and the only one benchmarked here.
    demucs_model: str

    #: Segment length in seconds for Demucs. Set explicitly because peak memory scales with it, and
    #: an out-of-memory kill fails the activity with no retry behind it.
    demucs_segment: int

    @staticmethod
    def load() -> "FunctionOptions":
        return FunctionOptions(
            staging_connection_string=_required("StagingStorageConnectionString"),
            media_connection_string=_required("MediaStorageConnectionString"),
            staging_container=_required("MediaProcessing:StagingContainerName"),
            media_container=_required("MediaProcessing:MediaContainerName"),
            callback_base_url=_required("CallbackBaseUrl").rstrip("/"),
            media_processing_api_key=_required("MediaProcessingApiKey"),
            ffmpeg_binary=_optional("FFMPEG_BINARY", "ffmpeg"),
            demucs_model=_optional("DEMUCS_MODEL", "htdemucs"),
            demucs_segment=int(_optional("DEMUCS_SEGMENT", "8")),
        )
