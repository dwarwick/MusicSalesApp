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

#: Long tracks are aligned in chunks, and the driver is self-attention rather than the emission
#: matrix.  wav2vec2 attends over every pair of frames, so cost grows with the SQUARE of the chunk:
#: at 16 kHz a frame is 20 ms, so 120 s is ~6000 frames and one attention matrix is 6000² x 16 heads
#: x 4 bytes ~ 2.3 GB, on top of a model that is already ~1.2 GB.  That was the value here, and on a
#: 4096 MB Flex instance - the largest the platform sells - it is an out-of-memory kill after
#: separation has already finished, which is the most expensive possible moment to fail.
#:
#: 45 s is ~2250 frames, or about a seventh of the attention cost.  Halving the chunk quarters it.
#:
#: This bounds MEMORY ONLY.  The chunks' emissions are concatenated and aligned as one, so this value
#: cannot affect where a word lands - which is the property that makes it safe to tune.
_CHUNK_SECONDS = 45

#: No overlap between model chunks, so the frames concatenate contiguously and frame index maps
#: linearly to time.  A frame's receptive field is tens of milliseconds, so a seam costs a little
#: accuracy for a word sitting exactly on one - against overlap, which would need those duplicate
#: frames discarded before concatenation, and a mistake there shifts every timing after it.
#:
#: The convolutional front end needs at least this many samples to emit a frame.
_MIN_CHUNK_SAMPLES = 4000


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

    # Mono, at the rate the bundle expects. Both are already true - this is handed
    # prep_audio.prepare_for_alignment's output, which converts the 44.1 kHz stereo stem Demucs
    # produces into 16 kHz mono - but the conversion is kept as a guard rather than an assertion,
    # because the alternative to noticing is aligning against one channel silently.
    if waveform.shape[0] > 1:
        waveform = waveform.mean(dim=0, keepdim=True)

    if sample_rate != bundle.sample_rate:
        waveform = torchaudio.functional.resample(waveform, sample_rate, bundle.sample_rate)
        sample_rate = bundle.sample_rate

    total_samples = waveform.shape[1]
    chunk_samples = _CHUNK_SECONDS * sample_rate

    # THE MODEL RUNS IN CHUNKS; THE ALIGNMENT DOES NOT. Only the first of those is expensive.
    #
    # Chunking the alignment as well - aligning each slice of audio against whatever transcript was
    # left over - looks equivalent and is not, because forced alignment is *forced*. CTC must emit a
    # monotonic path that consumes every target it is given, so handing a 45 s slice all 416 words of
    # a song obliges it to fit all 416 into those 45 seconds. It does, the words come out compressed
    # by four or five times, and nothing raises: the run reports "Aligned 416 of 416 tokens" and
    # every timing is wrong. Measured on a 4:07 track, the whole lyric landed inside the first 79
    # seconds with the closing line stretched across the remaining minute and a half.
    #
    # Concatenating the emissions instead costs almost nothing. An emission row is one probability
    # per vocabulary symbol - about 30 floats - so a four minute song is roughly 12,000 x 30, on the
    # order of a megabyte, against the ~300 MB of attention that producing it needed. Aligning once
    # over that lets CTC distribute the words across the real duration, which is the only way the
    # timings can be right.
    emissions = []

    with torch.inference_mode():
        offset = 0
        while offset < total_samples:
            end = min(total_samples, offset + chunk_samples)

            # The convolutional front end consumes a minimum number of samples before it produces a
            # frame at all, so a sliver left over at the end raises rather than returning nothing.
            # Dropping under a quarter of a second of trailing audio cannot move a word.
            if end - offset < _MIN_CHUNK_SAMPLES and emissions:
                break

            emission, _ = model(waveform[:, offset:end])
            emissions.append(emission[0])
            offset = end

        full_emission = torch.cat(emissions, dim=0) if len(emissions) > 1 else emissions[0]
        emissions.clear()

        # Drop anything the acoustic model has no symbol for, BEFORE measuring or aligning.
        # MMS_FA's dictionary is lowercase letters and the apostrophe, while normalize_word keeps
        # anything \w matches - digits included - so an ordinary lyric like "party like it's 1999"
        # reaches the tokenizer as `1999` and raises KeyError, failing the whole run after separation
        # has already been paid for. A dropped word is not a lost word: map_to_lines interpolates any
        # token the aligner did not place, which is the degradation this pipeline is built around.
        alignable = _drop_untokenizable(tokenizer, list(transcript_tokens))

        # Still measured, as a safety net rather than a working constraint: against a whole song's
        # emission there are several times more frames than characters, so this trims nothing for a
        # normal track. It is what stops a pathologically wordy or very short song from raising.
        words = _fit_to_emission(tokenizer, alignable, full_emission.shape[0])
        token_spans = aligner(full_emission, tokenizer(words))

    total_frames = full_emission.shape[0]
    samples_per_frame = total_samples / total_frames

    results: list[dict] = []

    for index, spans in enumerate(token_spans):
        if not spans:
            continue

        start_sample = spans[0].start * samples_per_frame
        end_sample = spans[-1].end * samples_per_frame

        results.append(
            {
                "norm": words[index],
                "startMs": int((start_sample / sample_rate) * 1000),
                "endMs": int((end_sample / sample_rate) * 1000),
                "score": float(sum(span.score for span in spans) / len(spans)),
            }
        )

    _logger.info(
        "Aligned %d of %d tokens across %d frames (%.1fs of audio).",
        len(results), len(transcript_tokens), total_frames, total_samples / sample_rate,
    )
    return results


def _drop_untokenizable(tokenizer, words: list[str]) -> list[str]:
    """Keep only the words the bundle's tokenizer can encode.

    Asked word by word rather than by inspecting the dictionary, because the dictionary's shape
    belongs to torchaudio and the question here is simply "will this raise".  ~400 calls on a song,
    against an alignment that takes minutes.
    """
    kept: list[str] = []
    dropped: list[str] = []

    for word in words:
        try:
            tokenizer([word])
        except Exception:  # noqa: BLE001 - anything it rejects is a word we cannot align.
            dropped.append(word)
            continue
        kept.append(word)

    if dropped:
        _logger.info(
            "Dropped %d of %d words the aligner has no symbols for (e.g. %s); they will be "
            "interpolated from their neighbours.",
            len(dropped), len(words), ", ".join(repr(word) for word in dropped[:5]),
        )

    return kept


def _fit_to_emission(tokenizer, words: list[str], frames: int) -> list[str]:
    """The longest prefix of ``words`` that CTC can align against ``frames`` of emission.

    The budget is ``symbols + adjacent repeats <= frames``, counted on the flattened token sequence -
    a repeat needs a blank between the two occurrences, so it costs an extra frame.  Counted
    incrementally, including across the join between one word's last symbol and the next word's
    first, which is a real adjacency once flattened and is easy to miss.

    Returns at least one word.  An empty return would place nothing, `consumed` would stay 0, and the
    caller would skip the chunk forever - so an over-tight budget would turn into silent total
    failure rather than a slightly short chunk.
    """
    budget = max(1, frames - 1)

    # No try/except here. It used to catch a tokenizer failure and return `words` unchanged, which
    # accomplished nothing at all: the caller tokenizes that same list on the very next line and
    # raises the identical exception, so the only effect was a warning claiming the run had carried
    # on when it had not. _drop_untokenizable now removes those words before this is reached, which
    # is a place the problem can actually be dealt with.
    encoded = tokenizer(words)

    symbols = 0
    repeats = 0
    previous = None
    kept = 0

    for token_ids in encoded:
        ids = list(token_ids)
        if not ids:
            kept += 1
            continue

        added_repeats = sum(1 for left, right in zip(ids, ids[1:]) if left == right)
        if previous is not None and ids[0] == previous:
            added_repeats += 1

        if symbols + len(ids) + repeats + added_repeats > budget:
            break

        symbols += len(ids)
        repeats += added_repeats
        previous = ids[-1]
        kept += 1

    if kept < len(words):
        _logger.info("Trimmed the transcript to %d of %d words for a %d-frame chunk.",
                     kept, len(words), frames)

    return words[:kept] if kept else words[:1]
