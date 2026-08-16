"""The two serialised artifacts: the JSON the player reads and the LRC an artist can take away."""

from __future__ import annotations

from lyrics.align_map import AlignedToken, map_to_lines
from lyrics.formats import _format_timestamp, to_enhanced_lrc, to_timing_json
from lyrics.normalize import tokenize_lyrics


def align(lyrics: str, aligned: list[AlignedToken], duration_ms: int = 0):
    lines, tokens = tokenize_lyrics(lyrics)
    return map_to_lines(lines, tokens, aligned, duration_ms=duration_ms)


class TestTimestampFormat:
    def test_it_is_minutes_seconds_hundredths(self):
        assert _format_timestamp(0) == "00:00.00"
        assert _format_timestamp(1_234) == "00:01.23"
        assert _format_timestamp(65_500) == "01:05.50"

    def test_minutes_are_not_wrapped_at_sixty(self):
        # A 75-minute mix is 75:00.00. Wrapping would make it indistinguishable from 15:00.00.
        assert _format_timestamp(75 * 60 * 1000) == "75:00.00"

    def test_negatives_are_clamped_rather_than_formatted(self):
        assert _format_timestamp(-500) == "00:00.00"


class TestTimingJson:
    def test_it_carries_the_song_id_duration_and_confidence(self):
        output = align("hello", [AlignedToken("hello", 0, 400)], duration_ms=214_000)
        document = to_timing_json(42, output)

        assert document["songId"] == 42
        assert document["durationMs"] == 214_000
        assert 0.0 <= document["confidence"] <= 1.0

    def test_the_display_text_is_exactly_what_the_artist_typed(self):
        # Normalisation is for matching only. If this ever regresses, listeners see lowercased,
        # depunctuated lyrics.
        output = align("Don't Look Back!", [AlignedToken("do", 0, 200), AlignedToken("not", 200, 400), AlignedToken("look", 400, 700), AlignedToken("back", 700, 1000)])
        document = to_timing_json(1, output)

        assert document["lines"][0]["text"] == "Don't Look Back!"
        assert [word["text"] for word in document["lines"][0]["words"]] == ["Don't", "Look", "Back!"]

    def test_untimed_lines_are_kept_with_nulls(self):
        # A section heading is part of how the artist laid the song out. Dropping it would mean the
        # display is not showing the lyrics that were submitted.
        output = align("[Chorus]\nhello", [AlignedToken("hello", 0, 400)])
        document = to_timing_json(1, output)

        assert document["lines"][0]["text"] == "[Chorus]"
        assert document["lines"][0]["startMs"] is None
        assert document["lines"][1]["startMs"] == 0

    def test_it_is_json_serialisable(self):
        import json

        output = align("hello there", [AlignedToken("hello", 0, 400)])
        json.dumps(to_timing_json(1, output))


class TestEnhancedLrc:
    def test_each_timed_line_starts_with_a_line_timestamp(self):
        output = align("hello there", [AlignedToken("hello", 1000, 1400), AlignedToken("there", 1500, 1900)])
        lrc = to_enhanced_lrc(output)

        assert lrc.startswith("[00:01.00]")

    def test_each_word_carries_an_inline_timestamp(self):
        output = align("hello there", [AlignedToken("hello", 1000, 1400), AlignedToken("there", 1500, 1900)])
        lrc = to_enhanced_lrc(output)

        assert "<00:01.00>hello" in lrc
        assert "<00:01.50>there" in lrc

    def test_optional_metadata_tags_are_emitted_first(self):
        output = align("hello", [AlignedToken("hello", 0, 400)])
        lrc = to_enhanced_lrc(output, title="Night Drive", artist="Someone")

        assert lrc.splitlines()[0] == "[ti:Night Drive]"
        assert lrc.splitlines()[1] == "[ar:Someone]"

    def test_an_untimed_line_is_emitted_without_a_timestamp(self):
        # A reader hitting one simply shows it, which is the wanted behaviour for a section heading.
        output = align("[Chorus]\nhello", [AlignedToken("hello", 0, 400)])
        lrc = to_enhanced_lrc(output)

        assert "[Chorus]" in lrc
        assert "[00:00.00]" in lrc

    def test_it_ends_with_a_newline(self):
        output = align("hello", [AlignedToken("hello", 0, 400)])
        assert to_enhanced_lrc(output).endswith("\n")
