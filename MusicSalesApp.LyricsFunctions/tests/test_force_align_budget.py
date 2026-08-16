"""Fitting the transcript to what CTC can actually align in one chunk.

Pure arithmetic over the tokenizer's output, so it runs without torch - which matters, because the
bug it prevents was found in Azure after eight minutes of separation had already been paid for.
"""

from __future__ import annotations

import pytest

from activities.force_align import _drop_untokenizable, _fit_to_emission

_ALPHABET = "abcdefghijklmnopqrstuvwxyz'"


def tokenizer(words: list[str]) -> list[list[int]]:
    """Stand-in for the MMS_FA tokenizer: one integer per character, as it does."""
    return [[ord(character) for character in word] for word in words]


class TestTheTranscriptIsTrimmedToFitTheChunk:
    def test_everything_fits_when_there_are_frames_to_spare(self):
        words = ["hello", "world"]

        assert _fit_to_emission(tokenizer, words, frames=500) == words

    def test_it_stops_at_the_last_word_that_fits(self):
        # 'aaa' + 'bbb' is 6 symbols and 4 repeats (two inside each word) = 10, so 11 frames is the
        # first budget that admits both.
        words = ["aaa", "bbb"]

        assert _fit_to_emission(tokenizer, words, frames=12) == ["aaa", "bbb"]
        assert _fit_to_emission(tokenizer, words, frames=8) == ["aaa"]

    def test_a_repeat_across_a_word_boundary_is_counted(self):
        # Flattened, 'ba' + 'ab' is b,a,a,b - the pair in the middle spans the join and still needs a
        # blank between them. Counting repeats only within each word undercounts the budget and lets
        # through a transcript that CTC then rejects, which is the exact failure being prevented.
        # Both pairs are 4 symbols; only the first also pays for the repeat at the join, so a budget
        # of 4 (frames=5) is exactly the width that tells the two apart.
        assert _fit_to_emission(tokenizer, ["ba", "ab"], frames=5) == ["ba"]
        assert _fit_to_emission(tokenizer, ["ba", "xy"], frames=5) == ["ba", "xy"]

    def test_it_never_returns_nothing(self):
        # An empty result places no words, so `consumed` stays 0 and the caller advances past the
        # chunk - forever, for every chunk. A budget too small for even one word must still yield one.
        assert _fit_to_emission(tokenizer, ["extraordinarily"], frames=1) == ["extraordinarily"]

    @pytest.mark.parametrize("frames", [2, 10, 50, 5000])
    def test_the_result_is_always_a_prefix(self, frames):
        # The caller indexes `remaining[index]` against the spans this produces, so anything other
        # than a prefix silently attaches timings to the wrong words.
        words = ["one", "two", "three", "four", "five"]
        fitted = _fit_to_emission(tokenizer, words, frames=frames)

        assert fitted == words[:len(fitted)]


class TestWordsTheAlignerCannotEncodeAreDropped:
    """MMS_FA's dictionary is lowercase letters and the apostrophe - nothing else."""

    @staticmethod
    def strict_tokenizer(words: list[str]) -> list[list[int]]:
        """Raises on anything outside the alphabet, as the real one does."""
        encoded = []
        for word in words:
            encoded.append([_ALPHABET.index(character) for character in word])  # ValueError if absent
        return encoded

    def test_a_word_with_digits_is_dropped_rather_than_failing_the_run(self):
        # normalize_word keeps digits because \w matches them, so "party like it's 1999" reaches the
        # tokenizer as `1999` and raises. Before this, that failed the entire alignment - after
        # separation had already cost eight minutes of compute.
        kept = _drop_untokenizable(self.strict_tokenizer, ["party", "like", "its", "1999"])

        assert kept == ["party", "like", "its"]

    def test_ordinary_words_all_survive(self):
        words = ["came", "home", "salty", "don't"]

        assert _drop_untokenizable(self.strict_tokenizer, words) == words

    def test_a_dropped_word_does_not_shift_the_others(self):
        # The caller reads word strings back out of this list by index, so the result has to stay in
        # order - a reordering would attach each timing to the wrong word.
        kept = _drop_untokenizable(self.strict_tokenizer, ["one", "2", "three", "4", "five"])

        assert kept == ["one", "three", "five"]
