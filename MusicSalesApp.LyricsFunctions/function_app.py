"""The lyrics-alignment Function app.

An HTTP-started Durable orchestration.  A creator pastes lyrics, the web app writes them to the media
container and asks Hangfire to start a run; Hangfire POSTs here, gets back an instance id and its
management URLs, and records them.  That id is the whole reason this is an HTTP starter rather than a
queue trigger: a queue trigger has no response channel, and without the id nothing can ask how a run
is going, stop one, or tell a failed run from a lost callback.

Two structural decisions differ from ``karaoke-lyrics-plan.md``, both because the plan's shape does
not survive contact with how Durable actually executes:

**The compute is one activity, not five.**  Durable makes no guarantee that two activities in the
same orchestration run on the same instance, so the plan's chain of ``prep_audio`` ->
``separate_vocals`` -> ``force_align`` cannot pass local file paths between its steps - each would
find the previous one's temp files missing.  The alternatives were to shuttle an 8 MB WAV and a vocal
stem through blob storage between every step, or to keep the chain local.  Local wins: it is the
step boundary that was hypothetical, whereas the files are real.

**Failure is reported by the orchestrator, not by a poison queue.**  A Durable trigger message is
deleted the moment the run is *scheduled*, so an orchestration that fails an hour later produces no
platform event at all.  The ``try/except`` below is the only thing that turns a failure into
something the web app hears about promptly - and the web app's reconciler, which can query the
instance id, is the second detector behind it.
"""

from __future__ import annotations

import json
import logging
import os
import shutil
import tempfile

import azure.durable_functions as df
import azure.functions as func

from activities import force_align, prep_audio, separate_vocals
from lyrics.align_map import AlignedToken, SilenceWindow, map_to_lines
from lyrics.constants import (
    ORCHESTRATOR_NAME,
    LyricsAlignmentFailureCodes,
    LyricsAlignmentOutcome,
    LyricsAlignmentStep,
    StagingPaths,
)
from lyrics.formats import to_enhanced_lrc, to_timing_json
from lyrics.normalize import tokenize_lyrics
from services.blob_store import BlobStore
from services.callback_client import CallbackClient
from services.options import FunctionOptions
from services.progress import ProgressReporter

app = df.DFApp(http_auth_level=func.AuthLevel.FUNCTION)

_logger = logging.getLogger(__name__)

#: Rough wall clock for separation on two cores, used only to animate the heartbeat. Being
#: approximately right beats a bar that does not move for twenty minutes.
_SEPARATION_ESTIMATE_SECONDS = 480

#: Every code this app can raise, used to recover one from a re-wrapped exception's message.
_ALL_FAILURE_CODES = tuple(
    value for name, value in vars(LyricsAlignmentFailureCodes).items()
    if not name.startswith("_") and isinstance(value, str)
)


class LyricsPipelineError(Exception):
    """A failure with a code the web app already knows how to describe to a creator.

    The code is stamped into the message text as a ``[Code]`` prefix, and that is not cosmetic: it is
    the only part of this exception that survives the activity -> orchestrator boundary.  Durable
    re-wraps whatever an activity raises, so ``self.code`` is gone by the time the orchestrator's
    except block runs and only the message text remains.  Without the prefix every failure - a
    missing audio blob, unusable lyrics, a Demucs crash - reaches the creator as the same generic
    "lyric timing failed".
    """

    def __init__(self, code: str, message: str) -> None:
        super().__init__(f"[{code}] {message}")
        self.code = code


# ---------------------------------------------------------------------------
# Starter
# ---------------------------------------------------------------------------


@app.route(route="lyrics/align", methods=["POST"])
@app.durable_client_input(client_name="client")
async def start_lyrics_alignment(req: func.HttpRequest, client) -> func.HttpResponse:
    """Schedule an alignment and hand back the instance id plus its management URLs.

    ``create_check_status_response`` is what returns ``statusQueryGetUri`` and ``terminatePostUri``
    alongside the id.  The web app stores those verbatim - their ``code`` is the durable-task
    extension's system key, which is a different secret from the function key used to reach this
    endpoint and one the web app has no other way to obtain.
    """
    try:
        payload = req.get_json()
    except ValueError:
        return func.HttpResponse(
            json.dumps({"message": "A JSON body is required."}),
            status_code=400,
            mimetype="application/json",
        )

    job_id = payload.get("jobId")
    if not job_id:
        return func.HttpResponse(
            json.dumps({"message": "A job id is required."}),
            status_code=400,
            mimetype="application/json",
        )

    instance_id = await client.start_new(ORCHESTRATOR_NAME, client_input=payload)
    _logger.info("Started lyrics alignment %s for job %s.", instance_id, job_id)

    return client.create_check_status_response(req, instance_id)


# ---------------------------------------------------------------------------
# Orchestrator
# ---------------------------------------------------------------------------


@app.orchestration_trigger(context_name="context")
def align_lyrics_orchestrator(context: df.DurableOrchestrationContext):
    """Run the alignment and make sure somebody hears about the outcome either way.

    Deterministic: no clock, no randomness, no I/O.  Everything that touches the world is an activity.

    The retry split is the important part.  ``align_lyrics`` is not retried - it is minutes of CPU,
    and its realistic failures are out-of-memory (which repeats at the same instance size) and audio
    it cannot make sense of (which is deterministic), so a second attempt costs another eight to
    forty minutes to fail the same way.  The two callback activities are retried hard, because they
    are cheap HTTP posts and they are the only things standing between a finished run and silence.
    """
    job = context.get_input()

    callback_retry = df.RetryOptions(first_retry_interval_in_milliseconds=30_000, max_number_of_attempts=5)

    try:
        result = yield context.call_activity("align_lyrics", job)
    except Exception as error:  # noqa: BLE001 - the whole point of this handler.
        # getattr(error, "code") does NOT survive here. Durable re-wraps whatever an activity raises
        # into a generic exception before the orchestrator sees it, so a LyricsPipelineError arrives
        # as a plain Exception with its attributes gone and only its message intact. Reading the code
        # back out of the message is what keeps "we could not isolate the vocals" from degrading into
        # a generic "lyric timing failed" by the time it reaches the creator.
        failure = {
            "jobId": job.get("jobId"),
            "outcome": _outcome_for(error),
            "failureCode": _recover_failure_code(error),
            "diagnostic": str(error)[:2000],
            "isMonotonic": True,
        }

        yield context.call_activity_with_retry("report_lyrics_result", callback_retry, failure)
        return failure

    yield context.call_activity_with_retry("report_lyrics_result", callback_retry, result)
    return result


def _recover_failure_code(error: Exception) -> str:
    """Read a LyricsPipelineError's code back out of the re-wrapped exception's text.

    Crude, and the alternative is worse: threading a structured result back through the activity
    return value means the orchestrator can no longer tell a failure from a success by whether it
    threw, which is the one thing Durable's retry policies key off.
    """
    code = getattr(error, "code", None)
    if code:
        return code

    # Matched bracketed, never bare. A diagnostic quoting Demucs stderr is free text that can
    # contain anything, and a bare substring test would let the word "AlignmentFailed" appearing in
    # a stack trace relabel an unrelated failure.
    text = str(error)
    for known in _ALL_FAILURE_CODES:
        if f"[{known}]" in text:
            return known

    return LyricsAlignmentFailureCodes.ORCHESTRATION_FAILED


def _outcome_for(error: Exception) -> str:
    """Bad input versus a pipeline that could not run - the same three-way split the audio pipeline uses.

    ``Unusable`` means the creator must be told and there is no point trying again unchanged.
    ``Inconclusive`` means the tooling failed rather than the submission, which is worth a Re-run.
    """
    code = _recover_failure_code(error)

    unusable = {
        LyricsAlignmentFailureCodes.LYRICS_TEXT_EMPTY,
        LyricsAlignmentFailureCodes.NO_TOKENS_MATCHED,
        LyricsAlignmentFailureCodes.AUDIO_BLOB_MISSING,
        LyricsAlignmentFailureCodes.LYRICS_BLOB_MISSING,
    }

    return LyricsAlignmentOutcome.UNUSABLE if code in unusable else LyricsAlignmentOutcome.INCONCLUSIVE


# ---------------------------------------------------------------------------
# Activities
# ---------------------------------------------------------------------------


@app.activity_trigger(input_name="job")
def align_lyrics(job: dict) -> dict:
    """Everything from downloading the audio to writing the timing artifacts.

    One activity because the stages share local files - see the module docstring.  The temp directory
    is removed unconditionally in ``finally``: Flex instances are reused between invocations, and
    local disk is per-instance and small.
    """
    options = FunctionOptions.load()
    store = BlobStore(options)
    reporter = ProgressReporter(CallbackClient(options), job["jobId"])

    work_dir = tempfile.mkdtemp(prefix="lyrics-")

    try:
        reporter.report(LyricsAlignmentStep.PREPARING, "Reading the song.")

        lyrics_text = store.read_media_text(job["lyricsBlobPath"])
        if lyrics_text is None:
            raise LyricsPipelineError(
                LyricsAlignmentFailureCodes.LYRICS_BLOB_MISSING,
                "The submitted lyrics could not be read back from storage.",
            )

        lines, tokens = tokenize_lyrics(lyrics_text)
        if not tokens:
            raise LyricsPipelineError(
                LyricsAlignmentFailureCodes.LYRICS_TEXT_EMPTY,
                "There were no words to align once section markers were removed.",
            )

        source_path = os.path.join(work_dir, "source.mp3")
        if not store.download_media_to(job["mp3BlobPath"], source_path):
            raise LyricsPipelineError(
                LyricsAlignmentFailureCodes.AUDIO_BLOB_MISSING,
                "This song's audio could not be read.",
            )

        # Separate FIRST, then prepare the aligner's copy from the stem. The order is the contract:
        # Demucs wants 44.1 kHz stereo and the aligner wants 16 kHz mono, and the silence map has to
        # be measured on the isolated vocal or it finds nothing an instrumental break looks like.
        try:
            mix_path = os.path.join(work_dir, "mix.wav")
            duration_ms = prep_audio.decode_for_separation(
                options.ffmpeg_binary, source_path, mix_path
            )
        except Exception as error:  # noqa: BLE001
            raise LyricsPipelineError(
                LyricsAlignmentFailureCodes.PREPARATION_FAILED, str(error)
            ) from error

        reporter.report(LyricsAlignmentStep.SEPARATING_VOCALS, "Isolating the vocal.")

        # Demucs offers no progress callback and is most of the wall clock, so the bar is kept alive
        # from here rather than left frozen for tens of minutes - which the web app would otherwise
        # read as a run that had stopped reporting.
        with reporter.heartbeat(
            LyricsAlignmentStep.SEPARATING_VOCALS,
            _SEPARATION_ESTIMATE_SECONDS,
            "Isolating the vocal.",
        ):
            try:
                vocal_path = separate_vocals.separate(
                    options.demucs_model,
                    options.demucs_segment,
                    mix_path,
                    os.path.join(work_dir, "stems"),
                    ffmpeg=options.ffmpeg_binary,
                    duration_ms=duration_ms,
                    chunk_seconds=options.demucs_chunk_seconds,
                    margin_seconds=options.demucs_chunk_margin_seconds,
                )
            except Exception as error:  # noqa: BLE001
                raise LyricsPipelineError(
                    LyricsAlignmentFailureCodes.SEPARATION_FAILED, str(error)
                ) from error

        # The 44.1 kHz mix has done its job and is the largest thing on a small local disk.
        try:
            os.remove(mix_path)
        except OSError:
            pass

        try:
            prepared = prep_audio.prepare_for_alignment(
                options.ffmpeg_binary, vocal_path, os.path.join(work_dir, "aligner.wav")
            )
        except Exception as error:  # noqa: BLE001
            raise LyricsPipelineError(
                LyricsAlignmentFailureCodes.PREPARATION_FAILED, str(error)
            ) from error

        reporter.report(LyricsAlignmentStep.ALIGNING, "Matching the words to the vocal.")

        try:
            aligned_raw = force_align.align(prepared.wav_path, [token.norm for token in tokens])
        except Exception as error:  # noqa: BLE001
            raise LyricsPipelineError(
                LyricsAlignmentFailureCodes.ALIGNMENT_FAILED, str(error)
            ) from error

        reporter.report(LyricsAlignmentStep.MAPPING, "Placing the words on the timeline.")

        output = map_to_lines(
            lines,
            tokens,
            [
                AlignedToken(
                    norm=item["norm"],
                    start_ms=item["startMs"],
                    end_ms=item["endMs"],
                    score=item["score"],
                )
                for item in aligned_raw
            ],
            silences=[
                SilenceWindow(start_ms=window["startMs"], end_ms=window["endMs"])
                for window in prepared.silences
            ],
            # The DECODE's duration, not the prepared vocal's. These timings are played back against
            # the original MP3, so that is the timeline they have to be clamped to. The vocal stem is
            # reassembled from separated chunks and can land a few milliseconds short of the source;
            # small enough not to matter on its own, but it is the source's length that is correct
            # here, and it is free to use it. The web app's structural gate compares LastWordEndMs
            # against this figure, so the two must describe the same timeline.
            duration_ms=duration_ms or prepared.duration_ms,
        )

        if output.stats.matched_token_count == 0:
            raise LyricsPipelineError(
                LyricsAlignmentFailureCodes.NO_TOKENS_MATCHED,
                "None of these words could be found in the song.",
            )

        reporter.report(LyricsAlignmentStep.WRITING_OUTPUTS, "Saving the timings.")

        timings_path = StagingPaths.timings(job["jobId"])
        lrc_path = StagingPaths.lrc(job["jobId"])

        store.upload_staged_text(
            timings_path,
            json.dumps(to_timing_json(job["songMetadataId"], output), ensure_ascii=False),
            "application/json; charset=utf-8",
        )
        store.upload_staged_text(
            lrc_path, to_enhanced_lrc(output), "text/plain; charset=utf-8"
        )

        stats = output.stats

        # Measurements only. Whether this is good enough to show a listener is decided server-side,
        # against an admin-tunable threshold - the Function reports facts, the web app judges.
        return {
            "jobId": job["jobId"],
            "outcome": LyricsAlignmentOutcome.ALIGNED,
            "timingsBlobPath": timings_path,
            "lrcBlobPath": lrc_path,
            "confidence": round(stats.confidence, 4),
            "meanAlignerConfidence": round(stats.mean_aligner_confidence, 4),
            "lyricTokenCount": stats.lyric_token_count,
            "matchedTokenCount": stats.matched_token_count,
            "interpolatedTokenCount": stats.interpolated_token_count,
            "droppedAlignerTokenCount": stats.dropped_aligner_token_count,
            "silenceViolationCount": stats.silence_violation_count,
            "lineCount": stats.line_count,
            "linesWithTimingCount": stats.lines_with_timing_count,
            "isMonotonic": stats.is_monotonic,
            "lastWordEndMs": stats.last_word_end_ms,
            "durationMs": stats.duration_ms,
        }
    finally:
        # Unconditional. A flag set after the expensive step would miss the case that matters most -
        # the run that failed partway through, holding a source file and a vocal stem.
        shutil.rmtree(work_dir, ignore_errors=True)


@app.activity_trigger(input_name="result")
def report_lyrics_result(result: dict) -> bool:
    """POST the terminal result. Raises on a non-2xx so the orchestrator's retry policy applies.

    Raising is deliberate and mirrors the C# Function app: swallowing this would mean a site that was
    restarting for thirty seconds costs the creator a whole alignment run, with the only trace being
    a debug log nobody reads.
    """
    options = FunctionOptions.load()
    CallbackClient(options).post_result(result)
    return True
