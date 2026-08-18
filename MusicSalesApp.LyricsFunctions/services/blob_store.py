"""Blob access, mirroring ``MusicSalesApp.Functions/Services/MediaBlobStore.cs``.

Note the asymmetry, which is the same one the C# app has and is load-bearing: **media is read-only
here, staging is read/write.**  The web app remains the sole writer of every primary blob and the
sole writer of the database.  This app reads a song's audio and the lyrics the artist pasted, writes
its derived output to staging, and reports what it did - it never touches a blob a row already points
at.

That is stricter than the C# app, which does write cover-art renditions straight into the media
container.  The exemption does not carry over: renditions go into a brand-new GUID folder before any
row exists, which is what makes them unreachable and therefore safe, whereas lyrics alignment is
re-runnable against a song that already exists and already has a row pointing at its timings.
"""

from __future__ import annotations

import logging
from functools import cached_property

from azure.core.exceptions import ResourceNotFoundError
from azure.storage.blob import BlobServiceClient

from .options import FunctionOptions

_logger = logging.getLogger(__name__)


class BlobStore:
    def __init__(self, options: FunctionOptions) -> None:
        self._options = options

    @cached_property
    def _staging(self):
        return BlobServiceClient.from_connection_string(
            self._options.staging_connection_string
        ).get_container_client(self._options.staging_container)

    @cached_property
    def _media(self):
        return BlobServiceClient.from_connection_string(
            self._options.media_connection_string
        ).get_container_client(self._options.media_container)

    def download_media_to(self, blob_path: str, destination_path: str) -> bool:
        """Pull a media blob down to local disk. False when it is not there.

        Returning rather than raising, because "the song was deleted while we were working on it" is
        a thing that happens and is worth reporting as a clean failure rather than a stack trace the
        orchestration has to interpret.
        """
        try:
            with open(destination_path, "wb") as handle:
                handle.write(self._media.get_blob_client(blob_path).download_blob().readall())
            return True
        except ResourceNotFoundError:
            _logger.warning("Media blob '%s' does not exist.", blob_path)
            return False

    def read_media_text(self, blob_path: str) -> str | None:
        """Read a media blob as UTF-8 text. None when it is not there."""
        try:
            data = self._media.get_blob_client(blob_path).download_blob().readall()
        except ResourceNotFoundError:
            _logger.warning("Media blob '%s' does not exist.", blob_path)
            return None

        return data.decode("utf-8-sig")

    def upload_staged_text(self, blob_path: str, content: str, content_type: str) -> None:
        """Write derived output to staging.

        Overwrites unconditionally, which is what makes an activity retry idempotent: the paths are
        a pure function of the attempt's job id, so a second attempt rewrites identical bytes rather
        than accumulating.
        """
        from azure.storage.blob import ContentSettings

        self._staging.get_blob_client(blob_path).upload_blob(
            content.encode("utf-8"),
            overwrite=True,
            content_settings=ContentSettings(content_type=content_type),
        )
