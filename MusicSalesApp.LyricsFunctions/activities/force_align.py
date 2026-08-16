"""Forced alignment: finding where in the vocal each word is sung.

Uses torchaudio's MMS_FA bundle, which is a CTC model plus a forced-alignment API - it takes audio
and a known transcript and returns per-token spans with scores.  That is exactly the shape of this
problem: the artist supplies the words, and only the timing is unknown.

**Sung vocals are much harder than speech**, and no amount of care here changes that.  Even good
pipelines land 150-300 ms average word-onset error, with a worse tail on melisma, harmonies, rap and
dense mixes.  The scores this returns are what the confidence figure is built from, and the whole
point of reporting them rather than hiding them is that some songs will simply come back wrong.
"""

from __future__ import annotations

import logging

_logger = logging.getLogger(__name__)

#: Long tracks are aligned in chunks: memory for the emission matrix scales with audio length, and a
#: ten-minute song in one pass is not obviously safe at 4 GB alongside the model itself.
_CHUNK_SECONDS = 120

#: Overlap so a word straddling a chunk boundary is seen whole by at least one chunk.
_CHUNK_OVERLAP_SECONDS = 5


def align(vocal_path: str, transcript_tokens: list[str]) -> list[dict]:
    """Align ``transcript_tokens`` against the vocal, returning one span per token.

    The tokens passed in are already normalised - lowercased, depunctuated, contractions expanded -
    because that is the form the acoustic model's vocabulary is in.  Mapping them back to what the
    artist actually typed is the next stage's job, not this one's.

    Imports are function-local on purpose: torch takes seconds to import and pulls in hundreds of
    megabytes, and the orchestrator's other activities have no use for it.  Paying that cost at module
    scope would add it to every invocation of every function in the app, including the HTTP starter
    that a creator is waiting on.
    """
    import torch
    import torchaudio

    if not transcript_tokens:
        return []

    bundle = torchaudio.pipelines.MMS_FA
    model = bundle.get_model()
    model.eval()

    tokenizer = bundle.get_tokenizer()
    aligner = bundle.get_aligner()

    waveform, sample_rate = torchaudio.load(vocal_path)

    # Mono, at the rate the bundle expects. Both are usually already true - prep_audio produced a
    # 16 kHz mono WAV and Demucs preserved that - but a stem that came back stereo would otherwise
    # align against one channel silently.
    if waveform.shape[0] > 1:
        waveform = waveform.mean(dim=0, keepdim=True)

    if sample_rate != bundle.sample_rate:
        waveform = torchaudio.functional.resample(waveform, sample_rate, bundle.sample_rate)
        sample_rate = bundle.sample_rate

    total_samples = waveform.shape[1]
    chunk_samples = _CHUNK_SECONDS * sample_rate
    overlap_samples = _CHUNK_OVERLAP_SECONDS * sample_rate

    results: list[dict] = []
    remaining = list(transcript_tokens)
    offset = 0

    with torch.inference_mode():
        while offset < total_samples and remaining:
            end = min(total_samples, offset + chunk_samples)
            chunk = waveform[:, offset:end]

            emission, _ = model(chunk)
            token_spans = aligner(emission[0], tokenizer(remaining))

            ratio = chunk.shape[1] / emission.shape[1]
            consumed = 0

            for index, spans in enumerate(token_spans):
                if not spans:
                    continue

                start_sample = offset + int(spans[0].start * ratio)
                end_sample = offset + int(spans[-1].end * ratio)

                # A span that runs to the very end of a non-final chunk is probably a word cut in
                # half by the boundary. Leave it for the next chunk, which sees it whole.
                if end < total_samples and end_sample >= end - overlap_samples:
                    break

                results.append(
                    {
                        "norm": remaining[index],
                        "startMs": int((start_sample / sample_rate) * 1000),
                        "endMs": int((end_sample / sample_rate) * 1000),
                        "score": float(sum(span.score for span in spans) / len(spans)),
                    }
                )
                consumed = index + 1

            if consumed == 0:
                # Nothing placed in this chunk - an instrumental stretch. Move on rather than
                # spinning: without this the loop cannot make progress and the activity runs to its
                # timeout.
                offset = end
                continue

            remaining = remaining[consumed:]
            offset = max(offset + 1, end - overlap_samples)

    _logger.info("Aligned %d of %d tokens.", len(results), len(transcript_tokens))
    return results
