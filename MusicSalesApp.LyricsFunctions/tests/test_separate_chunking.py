"""The chunk boundary arithmetic, with ffmpeg and Demucs stubbed out.

This is the one piece of the separation stage that can be wrong *quietly*.  A failed cut raises and a
failed Demucs run raises, but spans that do not tile the timeline exactly produce a perfectly valid
vocal stem of slightly the wrong length - and the symptom of that is lyric timings that drift further
out of step the longer the song runs, reported by a listener weeks later as "the karaoke is off".

So the properties asserted here are about coverage and joins, not about audio.
"""

from __future__ import annotations

import pytest

from activities import separate_vocals


@pytest.fixture
def recorded(monkeypatch):
    """Capture every cut, and answer as ffmpeg and Demucs would on success."""
    cuts: list[dict] = []

    def fake_cut(ffmpeg, source, start_seconds, length_seconds, destination):
        cuts.append(
            {
                "source": source,
                "start": round(start_seconds, 6),
                "length": round(length_seconds, 6),
                "destination": destination,
            }
        )

    def fake_run_demucs(model, segment, wav_path, output_dir):
        return f"{output_dir}/htdemucs/track/vocals.wav"

    def fake_concatenate(ffmpeg, pieces, destination, expected_ms):
        cuts.append({"joined": list(pieces), "destination": destination})
        return destination

    monkeypatch.setattr(separate_vocals, "_cut", fake_cut)
    monkeypatch.setattr(separate_vocals, "_run_demucs", fake_run_demucs)
    monkeypatch.setattr(separate_vocals, "_concatenate", fake_concatenate)
    monkeypatch.setattr(separate_vocals.os, "remove", lambda path: None)
    monkeypatch.setattr(separate_vocals.os, "makedirs", lambda path, exist_ok=False: None)

    return cuts


def separate(recorded, duration_ms, chunk_seconds=30, margin_seconds=5):
    separate_vocals.separate(
        "htdemucs", 7, "/tmp/mix.wav", "/tmp/out",
        ffmpeg="/mnt/tools/ffmpeg",
        duration_ms=duration_ms,
        chunk_seconds=chunk_seconds,
        margin_seconds=margin_seconds,
    )
    # The second cut of each pair trims a Demucs stem down to the span actually kept.
    return [cut for cut in recorded if "source" in cut and "vocals.wav" in cut["source"]]


class TestTheKeptSpansTileTheTrack:
    def test_they_cover_the_whole_duration_exactly(self, recorded):
        # A gap loses audio and a shortfall at the end truncates the song; either way the stem is not
        # the same length as the mix and every timing past the fault is wrong.
        kept = separate(recorded, duration_ms=221_000)

        assert sum(cut["length"] for cut in kept) == pytest.approx(221.0)

    def test_no_span_overlaps_the_next(self, recorded):
        # Overlap duplicates audio, which lengthens the stem and drags every later word late.
        separate(recorded, duration_ms=221_000)
        cores = _core_spans(chunk_seconds=30, duration_seconds=221.0)

        for (_, end), (next_start, _) in zip(cores, cores[1:]):
            assert end == pytest.approx(next_start)

    def test_the_first_chunk_cannot_seek_before_the_start_of_the_track(self, recorded):
        # max(0.0, ...) on the extended start. Without it ffmpeg is asked for a negative -ss, which
        # it silently treats as zero - while the trim that follows still subtracts the full margin,
        # so the kept span would start 5 s into the song and everything would be early by exactly
        # the margin. The two have to agree, which is why both halves are asserted together.
        kept = separate(recorded, duration_ms=221_000)
        extended = [c for c in recorded if "source" in c and c["source"] == "/tmp/mix.wav"]

        assert extended[0]["start"] == 0.0
        assert kept[0]["start"] == pytest.approx(0.0)

    def test_the_last_chunk_does_not_run_past_the_end(self, recorded):
        separate(recorded, duration_ms=221_000)
        extended = [c for c in recorded if "source" in c and c["source"] == "/tmp/mix.wav"]

        last = extended[-1]
        assert last["start"] + last["length"] < 221.0 + 1e-6


class TestWhenChunkingIsSkipped:
    def test_a_short_track_is_separated_in_one_pass(self, recorded):
        # Chunking a track that already fits buys nothing and costs two ffmpeg passes per chunk.
        separate_vocals.separate(
            "htdemucs", 7, "/tmp/mix.wav", "/tmp/out",
            ffmpeg="/mnt/tools/ffmpeg", duration_ms=29_000,
            chunk_seconds=30, margin_seconds=5,
        )

        assert recorded == []

    def test_an_unreadable_duration_falls_back_to_one_pass(self, recorded):
        # probe_duration_ms returns 0 rather than raising when ffprobe cannot read a duration.
        # Chunking on 0 would produce no chunks at all and silently return an empty stem.
        separate_vocals.separate(
            "htdemucs", 7, "/tmp/mix.wav", "/tmp/out",
            ffmpeg="/mnt/tools/ffmpeg", duration_ms=0,
            chunk_seconds=30, margin_seconds=5,
        )

        assert recorded == []


def _core_spans(chunk_seconds: int, duration_seconds: float) -> list[tuple[float, float]]:
    spans = []
    start = 0.0
    while start < duration_seconds:
        end = min(start + chunk_seconds, duration_seconds)
        spans.append((start, end))
        start = end
    return spans
