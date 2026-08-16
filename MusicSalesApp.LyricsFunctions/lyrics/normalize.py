"""Turning two very different token streams into something comparable.

The artist types ``Don't Look Back — I'm Ready!``; the aligner reports something closer to
``dont look back im ready``.  Neither is wrong, and neither can be compared to the other until both
have been put through the same reduction.

The one rule that governs everything here: **normalisation is for matching only, never for
display.**  Every normalised token keeps a pointer back to the exact characters the artist typed, and
that original is what ends up in the timing file.  A listener should see the artist's capitalisation
and punctuation, not ours.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass

# Expanded before punctuation is stripped, because the aligner hears the expansion.  Stripping first
# would turn "don't" into "dont", which matches nothing an acoustic model produces.
_CONTRACTIONS = {
    "ain't": "am not",
    "aren't": "are not",
    "can't": "cannot",
    "couldn't": "could not",
    "didn't": "did not",
    "doesn't": "does not",
    "don't": "do not",
    "hadn't": "had not",
    "hasn't": "has not",
    "haven't": "have not",
    "he'd": "he would",
    "he'll": "he will",
    "he's": "he is",
    "i'd": "i would",
    "i'll": "i will",
    "i'm": "i am",
    "i've": "i have",
    "isn't": "is not",
    "it'd": "it would",
    "it'll": "it will",
    "it's": "it is",
    "let's": "let us",
    "mustn't": "must not",
    "shan't": "shall not",
    "she'd": "she would",
    "she'll": "she will",
    "she's": "she is",
    "shouldn't": "should not",
    "that's": "that is",
    "there's": "there is",
    "they'd": "they would",
    "they'll": "they will",
    "they're": "they are",
    "they've": "they have",
    "wasn't": "was not",
    "we'd": "we would",
    "we'll": "we will",
    "we're": "we are",
    "we've": "we have",
    "weren't": "were not",
    "what's": "what is",
    "where's": "where is",
    "who's": "who is",
    "won't": "will not",
    "wouldn't": "would not",
    "you'd": "you would",
    "you'll": "you will",
    "you're": "you are",
    "you've": "you have",
}

# Section markers a lyric sheet is full of and a singer never sings.  Left in, they become lyric
# tokens with no counterpart in the audio, which drags the matched-token ratio down and is the single
# most common cause of a needlessly low confidence score.
#
# The two directions of this test are NOT equally costly, which is why there are two patterns rather
# than one permissive one.  Failing to spot a marker costs a little confidence.  Mistaking a sung
# line for one removes it from the token stream entirely, so it can never be timed and the listener
# watches it stay dark while it is being sung.  Lines like "chorus of angels singing" and "solo
# dancer in the rain" are exactly the trap.
_SECTION_KEYWORDS = (
    r"(?:intro|verse|pre-?chorus|chorus|bridge|outro|hook|refrain|solo"
    r"|instrumental|interlude|breakdown|spoken|ad-?libs?)"
)

# Bracketed, so the artist has already said this is an annotation.  The keyword may appear anywhere
# inside - "[Guitar Solo]", "[Repeat Chorus]".
_BRACKETED_MARKER = re.compile(
    rf"^\s*[\[\(][^\]\)]*\b{_SECTION_KEYWORDS}\b[^\]\)]*[\]\)]\s*$", re.IGNORECASE
)

# Unbracketed, so the reading has to be conservative. Requiring the line to be nothing BUT the
# keyword - "Chorus", "Verse 2", "Bridge:" - is safe and misses the very common qualified forms:
# a real submission carried "Final Chorus" and "Anthem Hook Section", both of which were then handed
# to the aligner as though somebody sang them. Each consumed several seconds of the timeline and
# appeared to a listener as a lyric line.
#
# So instead of loosening the keyword match - which is what reintroduces "chorus of angels singing" -
# the line must be SHORT and made ENTIRELY of words from this vocabulary, at least one of them an
# actual section keyword. "Final Chorus" and "Anthem Hook Section" pass; "chorus of angels singing"
# fails on "of", and "solo dancer in the rain" fails on length before anything else is considered.
_SECTION_MODIFIERS = (
    r"(?:final|last|first|second|third|fourth|main|alt|alternate|repeat|reprise|part|section"
    r"|anthem|opening|closing|ending|extended|double|big|end)"
)

_BARE_MARKER_WORD = re.compile(
    rf"^(?:{_SECTION_KEYWORDS}|{_SECTION_MODIFIERS}|\d+|[ivx]+)$", re.IGNORECASE
)

_BARE_MARKER_KEYWORD = re.compile(rf"^{_SECTION_KEYWORDS}$", re.IGNORECASE)

#: Beyond this many words a line is prose, whatever it is made of.
_MAX_BARE_MARKER_WORDS = 4

# Repeat annotations - "(x2)", "[2x]" - are instructions to the reader, not words.
_REPEAT_MARKER = re.compile(r"^[\[\(]?\s*[x×]\s*\d+\s*[\]\)]?$|^[\[\(]?\s*\d+\s*[x×]\s*[\]\)]?$", re.IGNORECASE)

_WORD_SPLIT = re.compile(r"\s+")


@dataclass(frozen=True)
class LyricToken:
    """One word of the artist's lyrics, in both forms and with its place in the sheet."""

    text: str
    """Exactly as the artist typed it, punctuation and capitals intact.  This is what is displayed."""

    norm: str
    """Reduced for matching.  Never displayed."""

    line_index: int
    word_index: int


@dataclass(frozen=True)
class LyricLine:
    """One line of the artist's lyrics, kept whole for display."""

    index: int
    text: str


def normalize_word(word: str) -> str:
    """Reduce one word to its matchable form.

    Accents are folded because an aligner trained on English will not reliably reproduce them, and a
    lyric sheet may or may not carry them for the same word.
    """
    folded = unicodedata.normalize("NFKD", word)
    folded = "".join(ch for ch in folded if not unicodedata.combining(ch))
    folded = folded.lower()

    # Keep the apostrophe for now: contraction expansion below still needs it.
    folded = re.sub(r"[^\w'’]+", "", folded, flags=re.UNICODE)
    folded = folded.replace("’", "'")
    return folded


def _expand_contractions(word: str) -> list[str]:
    expansion = _CONTRACTIONS.get(word)
    if expansion:
        return expansion.split()
    # A possessive or a contraction we do not know - drop the apostrophe and treat it as one word.
    return [word.replace("'", "")] if word else []


def is_section_marker(line: str) -> bool:
    """Whether a line is an annotation for the reader rather than something anybody sings."""
    stripped = line.strip()
    if not stripped:
        return False
    return bool(
        _BRACKETED_MARKER.match(stripped)
        or _is_bare_marker(stripped)
        or _REPEAT_MARKER.match(stripped)
    )


def _is_bare_marker(stripped: str) -> bool:
    """An unbracketed annotation: short, and every word drawn from the section vocabulary."""
    text = stripped.rstrip(":").strip()
    if not text:
        return False

    words = [word for word in _WORD_SPLIT.split(text) if word]
    if not words or len(words) > _MAX_BARE_MARKER_WORDS:
        return False

    # At least one real section word, so "Final Part" or "Last Section" - which name no section -
    # are left as lyrics rather than guessed at.
    if not any(_BARE_MARKER_KEYWORD.match(word) for word in words):
        return False

    return all(_BARE_MARKER_WORD.match(word) for word in words)


def tokenize_lyrics(text: str) -> tuple[list[LyricLine], list[LyricToken]]:
    """Split the artist's lyrics into displayable lines and matchable tokens.

    Blank lines and section markers are kept out of the token stream but **not** out of the line
    list: they are part of how the artist laid the song out, and a display that silently dropped
    them would not be showing the lyrics they submitted.  They simply end up with no timing.
    """
    lines: list[LyricLine] = []
    tokens: list[LyricToken] = []

    for line_index, raw_line in enumerate(text.split("\n")):
        lines.append(LyricLine(index=line_index, text=raw_line.rstrip()))

        if not raw_line.strip() or is_section_marker(raw_line):
            continue

        word_index = 0
        for raw_word in _WORD_SPLIT.split(raw_line.strip()):
            if not raw_word:
                continue

            normalized = normalize_word(raw_word)
            if not normalized:
                # Punctuation on its own - an em dash on a line of its own, say.  It stays in the
                # display text via the line above and simply never gets a timing of its own.
                continue

            for piece in _expand_contractions(normalized):
                if not piece:
                    continue
                tokens.append(
                    LyricToken(
                        text=raw_word,
                        norm=piece,
                        line_index=line_index,
                        word_index=word_index,
                    )
                )
            word_index += 1

    return lines, tokens
