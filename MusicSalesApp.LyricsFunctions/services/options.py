"""Settings, read once at import.

Mirrors ``MusicSalesApp.Functions/FunctionOptions.cs``, including its naming split: connection
strings and callback settings are top level, container names are grouped under a
``MediaProcessing__`` prefix.

Double underscore rather than the colon the C# app uses, because Flex Consumption rejects an app
setting whose name contains a colon - the whole settings push fails, not just that entry. ``__`` is
the documented separator on Linux App Service, and nothing here binds hierarchically anyway: these
are read as flat environment variables.

Everything else keeps the same name the C# app uses, so one settings helper can serve both Function
apps without maintaining two vocabularies for the same values.
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
    #:
    #: Bounded ABOVE by the model, not only by memory. The default htdemucs is a hybrid transformer,
    #: and Demucs refuses to run one past the segment it was trained for - 7.8 seconds - with a FATAL
    #: rather than a warning or a silent clamp. Anything over that fails 100% of the time, and does so
    #: only after downloading the weights, so it reads as an expensive infrastructure problem rather
    #: than a one-character config error.
    demucs_segment: int

    #: Seconds of audio separated per Demucs invocation. Demucs' peak memory tracks the length of
    #: the whole track - it holds full-length tensors for all four sources - and Flex Consumption
    #: allows no instance larger than 4096 MB, so a long track has to be broken up or it is killed
    #: outright. Measured: 29 s separates comfortably on a 4 GB instance, 3 min 41 s does not.
    demucs_chunk_seconds: int

    #: Extra seconds separated either side of each chunk and then thrown away. The model gets real
    #: audio as context across every boundary instead of silence, and only the interior it was most
    #: confident about is kept. Zero would leave an audible seam at each join, which the aligner
    #: reads as a consonant.
    demucs_chunk_margin_seconds: int

    @staticmethod
    def load() -> "FunctionOptions":
        return FunctionOptions(
            staging_connection_string=_required("StagingStorageConnectionString"),
            media_connection_string=_required("MediaStorageConnectionString"),
            staging_container=_required("MediaProcessing__StagingContainerName"),
            media_container=_required("MediaProcessing__MediaContainerName"),
            callback_base_url=_required("CallbackBaseUrl").rstrip("/"),
            media_processing_api_key=_required("MediaProcessingApiKey"),
            ffmpeg_binary=_optional("FFMPEG_BINARY", "ffmpeg"),
            demucs_model=_optional("DEMUCS_MODEL", "htdemucs"),
            demucs_segment=int(_optional("DEMUCS_SEGMENT", "7")),
            demucs_chunk_seconds=int(_optional("DEMUCS_CHUNK_SECONDS", "30")),
            demucs_chunk_margin_seconds=int(_optional("DEMUCS_CHUNK_MARGIN_SECONDS", "5")),
        )
