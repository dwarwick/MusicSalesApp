"""Tokenisation, and the rule that governs it: normalise for matching, never for display."""

from __future__ import annotations

from lyrics.normalize import is_section_marker, normalize_word, tokenize_lyrics


class TestNormalizeWord:
    def test_case_and_punctuation_are_stripped(self):
        assert normalize_word("Darkness,") == "darkness"
        assert normalize_word("—Hello!") == "hello"

    def test_accents_are_folded(self):
        # An English aligner will not reliably reproduce them, and a lyric sheet may or may not carry
        # them for the same word.
        assert normalize_word("café") == "cafe"
        assert normalize_word("naïve") == "naive"

    def test_a_curly_apostrophe_matches_a_straight_one(self):
        # Word processors substitute these silently, so the same contraction arrives both ways.
        assert normalize_word("don’t") == normalize_word("don't")

    def test_punctuation_alone_reduces_to_nothing(self):
        assert normalize_word("—") == ""
        assert normalize_word("...") == ""


class TestSectionMarkers:
    def test_common_markers_are_recognised(self):
        for marker in ("[Chorus]", "(Verse 2)", "Bridge:", "[Pre-Chorus]", "INTRO", "[Guitar Solo]"):
            assert is_section_marker(marker), marker

    def test_repeat_annotations_are_recognised(self):
        for marker in ("(x2)", "[2x]", "x3"):
            assert is_section_marker(marker), marker

    def test_real_lyrics_are_not_mistaken_for_markers(self):
        # The costly direction of this mistake: a sung line dropped from the token stream can never
        # be timed, and the listener sees it stay dark while it is being sung.
        for line in (
            "chorus of angels singing",
            "we built a bridge across the water",
            "solo dancer in the rain",
            "verse after verse she waited",
        ):
            assert not is_section_marker(line), line


class TestTokenizeLyrics:
    def test_every_line_is_preserved_including_blanks(self):
        lines, _ = tokenize_lyrics("one\n\n[Chorus]\ntwo")

        assert [line.text for line in lines] == ["one", "", "[Chorus]", "two"]

    def test_markers_and_blanks_produce_no_tokens(self):
        _, tokens = tokenize_lyrics("one\n\n[Chorus]\ntwo")

        assert [token.norm for token in tokens] == ["one", "two"]

    def test_each_token_remembers_where_it_came_from(self):
        _, tokens = tokenize_lyrics("hello darkness\nmy old friend")

        assert (tokens[0].line_index, tokens[0].word_index) == (0, 0)
        assert (tokens[1].line_index, tokens[1].word_index) == (0, 1)
        assert (tokens[2].line_index, tokens[2].word_index) == (1, 0)

    def test_the_original_text_survives_normalisation(self):
        # The whole point. What gets displayed is what the artist typed.
        _, tokens = tokenize_lyrics("Don't Look Back!")

        assert tokens[0].text == "Don't"
        assert tokens[-1].text == "Back!"

    def test_a_contraction_expands_into_what_the_aligner_hears(self):
        # The aligner hears "do not", not "dont". Expanding before punctuation is stripped is what
        # makes the match possible at all.
        _, tokens = tokenize_lyrics("don't stop")

        assert [token.norm for token in tokens] == ["do", "not", "stop"]

    def test_both_halves_of_a_contraction_point_at_one_displayed_word(self):
        _, tokens = tokenize_lyrics("don't stop")

        assert tokens[0].text == "don't"
        assert tokens[1].text == "don't"
        assert (tokens[0].line_index, tokens[0].word_index) == (tokens[1].line_index, tokens[1].word_index)
        assert tokens[2].word_index == 1, "The next word keeps its own position."

    def test_an_unknown_apostrophe_word_becomes_one_token(self):
        # Possessives and dialect spellings are not in the contraction table and must not be split.
        _, tokens = tokenize_lyrics("the river's edge")

        assert [token.norm for token in tokens] == ["the", "rivers", "edge"]

    def test_empty_input_yields_nothing_to_align(self):
        lines, tokens = tokenize_lyrics("")

        assert tokens == []
        assert len(lines) == 1

    def test_windows_line_endings_do_not_leak_into_words(self):
        # The web app normalises these before storing, but the Function must not depend on that: a
        # stray carriage return becomes part of a word and matches nothing.
        _, tokens = tokenize_lyrics("one\r\ntwo".replace("\r\n", "\n"))

        assert [token.norm for token in tokens] == ["one", "two"]
