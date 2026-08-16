"""The mapping algorithm, exercised against the things that actually go wrong.

Synthetic aligner output throughout, on purpose.  A test that needs a model to run is a test nobody
runs, and every property asserted here is a property of the mapping - not of the aligner.
"""

from __future__ import annotations

import pytest

from lyrics.align_map import AlignedToken, SilenceWindow, map_to_lines
from lyrics.normalize import tokenize_lyrics


def align(lyrics: str, aligned: list[AlignedToken], **kwargs):
    lines, tokens = tokenize_lyrics(lyrics)
    return map_to_lines(lines, tokens, aligned, **kwargs)


def tokens_from(words: str, start_ms: int = 0, step: int = 500, score: float = 0.95):
    """Evenly spaced aligner output for a run of words."""
    return [
        AlignedToken(norm=word, start_ms=start_ms + (index * step), end_ms=start_ms + (index * step) + step - 50, score=score)
        for index, word in enumerate(words.split())
    ]


class TestPerfectAlignment:
    def test_every_word_gets_a_time(self):
        output = align("hello darkness my old friend", tokens_from("hello darkness my old friend"))

        words = output.lines[0].words
        assert [word.text for word in words] == ["hello", "darkness", "my", "old", "friend"]
        assert all(word.start_ms is not None for word in words)

    def test_nothing_is_interpolated(self):
        output = align("hello darkness", tokens_from("hello darkness"))

        assert output.stats.matched_token_count == 2
        assert output.stats.interpolated_token_count == 0

    def test_the_line_spans_its_first_and_last_word(self):
        output = align("hello darkness", tokens_from("hello darkness", start_ms=1000, step=500))

        line = output.lines[0]
        assert line.start_ms == 1000
        assert line.end_ms == line.words[-1].end_ms


class TestDroppedWords:
    def test_a_word_the_aligner_missed_is_still_displayed(self):
        # The singer swallowed it, or the model did. It must not vanish from the lyric sheet.
        output = align(
            "one two three four",
            [
                AlignedToken("one", 0, 400),
                AlignedToken("two", 500, 900),
                # "three" missing entirely
                AlignedToken("four", 1500, 1900),
            ],
        )

        assert [word.text for word in output.lines[0].words] == ["one", "two", "three", "four"]

    def test_a_missed_word_is_interpolated_between_its_neighbours(self):
        output = align(
            "one two three four",
            [
                AlignedToken("one", 0, 400),
                AlignedToken("two", 500, 900),
                AlignedToken("four", 1500, 1900),
            ],
        )

        three = output.lines[0].words[2]
        assert three.start_ms is not None
        assert 900 <= three.start_ms <= 1500
        assert output.stats.interpolated_token_count == 1

    def test_several_consecutive_missed_words_are_spread_evenly(self):
        output = align(
            "one two three four five",
            [AlignedToken("one", 0, 100), AlignedToken("five", 4000, 4100)],
        )

        middle = [word.start_ms for word in output.lines[0].words[1:4]]
        assert middle == sorted(middle), "Interpolated words must stay in order."
        assert len(set(middle)) == 3, "They must be spread, not stacked on one instant."


class TestExtraWords:
    def test_ad_libs_the_sheet_does_not_contain_are_dropped(self):
        # "yeah" is not in the lyrics. It must not appear in the output, and it must not shift the
        # words around it.
        output = align(
            "one two three",
            [
                AlignedToken("one", 0, 400),
                AlignedToken("yeah", 450, 600),
                AlignedToken("two", 700, 1100),
                AlignedToken("three", 1200, 1600),
            ],
        )

        assert [word.text for word in output.lines[0].words] == ["one", "two", "three"]
        assert output.stats.dropped_aligner_token_count == 1
        assert output.lines[0].words[1].start_ms == 700

    def test_a_completely_unrelated_transcript_matches_nothing(self):
        output = align("alpha bravo charlie", tokens_from("xylophone quixotic zeppelin"))

        assert output.stats.matched_token_count == 0


class TestRepeatedChorus:
    def test_the_second_chorus_maps_to_the_later_occurrence(self):
        # The classic failure: a naive nearest-match snaps the second chorus back onto the first,
        # producing timings that run backwards halfway through the song. SequenceMatcher's blocks are
        # non-crossing, which is precisely why it is used here.
        lyrics = "we are free\nverse words here\nwe are free"

        aligned = (
            tokens_from("we are free", start_ms=0)
            + tokens_from("verse words here", start_ms=5000)
            + tokens_from("we are free", start_ms=10000)
        )

        output = align(lyrics, aligned)

        first_chorus = output.lines[0]
        second_chorus = output.lines[2]

        assert first_chorus.start_ms is not None and second_chorus.start_ms is not None
        assert second_chorus.start_ms > first_chorus.start_ms
        assert second_chorus.start_ms >= 10000

    def test_three_identical_lines_stay_in_order(self):
        lyrics = "na na na\nna na na\nna na na"
        aligned = (
            tokens_from("na na na", start_ms=0)
            + tokens_from("na na na", start_ms=4000)
            + tokens_from("na na na", start_ms=8000)
        )

        output = align(lyrics, aligned)
        starts = [line.start_ms for line in output.lines]

        assert starts == sorted(starts)
        assert len(set(starts)) == 3


class TestInstrumentalSections:
    def test_no_word_is_left_inside_a_detected_silence(self):
        # The drift this exists to kill: the aligner loses the thread over a solo and smears the
        # following words backwards across it.
        silence = SilenceWindow(start_ms=2000, end_ms=8000)

        output = align(
            "before after",
            [AlignedToken("before", 1000, 1400), AlignedToken("after", 5000, 5400)],
            silences=[silence],
        )

        after = output.lines[0].words[1]
        assert not silence.contains(after.start_ms)
        assert output.stats.silence_violation_count == 1

    def test_a_word_just_inside_the_start_of_a_break_is_pulled_back(self):
        # Nearer edge wins, so a trailing syllable rejoins the phrase it belongs to rather than being
        # thrown forward past the whole instrumental. It has to END where the break begins - landing
        # its start on that boundary would leave it inside the window it was being taken out of.
        window = SilenceWindow(start_ms=2000, end_ms=9000)

        output = align("hello", [AlignedToken("hello", 2100, 2400)], silences=[window])

        word = output.lines[0].words[0]
        assert word.end_ms == 2000
        assert word.start_ms == 1700, "The word keeps its width, ending as the break starts."
        assert not window.contains(word.start_ms)

    def test_words_outside_every_break_are_left_alone(self):
        output = align(
            "one two",
            [AlignedToken("one", 100, 400), AlignedToken("two", 600, 900)],
            silences=[SilenceWindow(start_ms=5000, end_ms=9000)],
        )

        assert output.stats.silence_violation_count == 0
        assert output.lines[0].words[0].start_ms == 100


class TestMonotonicity:
    def test_timings_never_run_backwards(self):
        output = align(
            "one two three",
            [
                AlignedToken("one", 1000, 1400),
                AlignedToken("two", 200, 600),  # the aligner contradicting itself
                AlignedToken("three", 2000, 2400),
            ],
        )

        starts = [word.start_ms for word in output.lines[0].words]
        assert starts == sorted(starts)

    def test_a_correction_is_reported_rather_than_hidden(self):
        # The numbers are fixed either way, but a result that needed fixing is one the aligner
        # contradicted itself on - which the server uses as a hard structural failure.
        output = align(
            "one two",
            [AlignedToken("one", 1000, 1400), AlignedToken("two", 200, 600)],
        )

        assert output.stats.is_monotonic is False

    def test_a_clean_result_reports_monotonic(self):
        output = align("one two", tokens_from("one two"))
        assert output.stats.is_monotonic is True


class TestDurationClamping:
    def test_nothing_is_placed_past_the_end_of_the_track(self):
        output = align(
            "one two",
            [AlignedToken("one", 1000, 1400), AlignedToken("two", 9000, 9400)],
            duration_ms=5000,
        )

        assert output.stats.last_word_end_ms <= 5000

    def test_a_trailing_run_is_anchored_rather_than_extrapolated(self):
        # Extrapolating outward past the last thing actually heard is how words end up past the end
        # of the song.
        output = align(
            "one two three",
            [AlignedToken("one", 1000, 1400)],
            duration_ms=200000,
        )

        assert output.stats.last_word_end_ms <= 1400


class TestSectionMarkers:
    def test_a_chorus_marker_is_displayed_but_never_matched(self):
        # Left in the token stream it would be an unmatchable lyric token, dragging coverage - and
        # therefore confidence - down for a song that aligned perfectly well.
        lyrics = "[Chorus]\nwe are free"
        output = align(lyrics, tokens_from("we are free"))

        assert output.lines[0].text == "[Chorus]"
        assert output.lines[0].words == []
        assert output.stats.matched_token_count == 3
        assert output.stats.confidence > 0.8

    def test_a_blank_line_is_preserved_with_no_timing(self):
        output = align("one\n\ntwo", tokens_from("one two"))

        assert len(output.lines) == 3
        assert output.lines[1].text == ""
        assert output.lines[1].start_ms is None


class TestContractions:
    def test_a_contraction_is_displayed_once_and_spans_both_halves(self):
        # "don't" is matched as "do" + "not" because that is what the aligner hears, but the artist
        # typed one word and one word is what gets displayed.
        output = align(
            "don't stop",
            [
                AlignedToken("do", 0, 300),
                AlignedToken("not", 350, 600),
                AlignedToken("stop", 700, 1100),
            ],
        )

        words = output.lines[0].words
        assert [word.text for word in words] == ["don't", "stop"]
        assert words[0].start_ms == 0
        assert words[0].end_ms == 600


class TestConfidence:
    def test_a_perfect_alignment_scores_high(self):
        output = align("one two three four", tokens_from("one two three four", score=1.0))
        assert output.stats.confidence > 0.95

    def test_poor_coverage_drags_the_score_down(self):
        good = align("one two three four", tokens_from("one two three four", score=0.9))
        poor = align(
            "one two three four",
            [AlignedToken("one", 0, 400, 0.9)],
        )

        assert poor.stats.confidence < good.stats.confidence

    def test_low_aligner_scores_drag_it_down(self):
        confident = align("one two three", tokens_from("one two three", score=0.95))
        unsure = align("one two three", tokens_from("one two three", score=0.3))

        assert unsure.stats.confidence < confident.stats.confidence

    def test_words_shoved_out_of_breaks_drag_it_down(self):
        clean = align("one two three four five", tokens_from("one two three four five", score=0.9))

        messy = align(
            "one two three four five",
            tokens_from("one two three four five", start_ms=3000, step=200, score=0.9),
            silences=[SilenceWindow(start_ms=2000, end_ms=9000)],
        )

        assert messy.stats.confidence < clean.stats.confidence

    def test_nothing_matching_scores_zero(self):
        output = align("alpha bravo", tokens_from("zeta omega"))
        assert output.stats.confidence == pytest.approx(0.0)

    def test_the_score_stays_within_range(self):
        for score in (0.0, 0.5, 1.0):
            output = align("one two three", tokens_from("one two three", score=score))
            assert 0.0 <= output.stats.confidence <= 1.0


class TestDegenerateInput:
    def test_empty_lyrics_do_not_raise(self):
        output = align("", tokens_from("anything"))
        assert output.stats.lyric_token_count == 0
        assert output.stats.confidence == 0.0

    def test_no_aligner_output_leaves_every_word_untimed(self):
        output = align("one two three", [])

        assert output.stats.matched_token_count == 0
        assert all(word.start_ms is None for word in output.lines[0].words)
        assert output.stats.lines_with_timing_count == 0

    def test_the_stats_count_the_lines_the_artist_typed(self):
        output = align("one\ntwo\nthree", tokens_from("one two three"))
        assert output.stats.line_count == 3
        assert output.stats.lines_with_timing_count == 3


class TestLongInstrumentalBridge:
    def test_a_verse_after_a_long_bridge_lands_after_it(self):
        lyrics = "first verse here\n[Solo]\nsecond verse here"

        aligned = tokens_from("first verse here", start_ms=0) + tokens_from(
            "second verse here", start_ms=60000
        )

        output = align(
            lyrics,
            aligned,
            silences=[SilenceWindow(start_ms=3000, end_ms=59000)],
            duration_ms=90000,
        )

        assert output.lines[2].start_ms is not None
        assert output.lines[2].start_ms >= 59000
        assert output.stats.is_monotonic is True
