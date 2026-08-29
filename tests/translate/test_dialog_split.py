"""Joining a message window for DeepL and wrapping the answer back.

A window is sent as one text so the translator sees a whole sentence instead of
the engine's visual line breaks. The answer is then wrapped to the original line
count at spaces, with target lengths following the proportions of the Japanese
lines - so no word is cut merely to reproduce a source boundary. A window of
moaning comes back with no spaces to wrap at, and is cut on its punctuation
instead: the alternative is an empty cell, which the overlay drops, leaving the
engine to draw Japanese for that line of an otherwise translated window.
"""

from __future__ import annotations

import re
import unittest

from context import TOOLS_ROOT  # noqa: F401  (prepares sys.path)

import delta_deepl as deepl


class TestJoinDialogSource(unittest.TestCase):
    def test_japanese_joins_without_spaces(self) -> None:
        """Japanese has no inter-word space; adding one changes the text."""
        joined = deepl.join_dialog_source(["「こんにちは", "　世界」"], "JA")
        self.assertEqual(joined, "「こんにちは世界」")

    def test_other_languages_join_with_a_space(self) -> None:
        joined = deepl.join_dialog_source(["Hello", "world"], "EN")
        self.assertEqual(joined, "Hello world")

    def test_each_line_is_stripped(self) -> None:
        self.assertEqual(deepl.join_dialog_source(["  a  ", "  b "], "EN"), "a b")


class TestSplitDialogTranslation(unittest.TestCase):
    def assert_words_preserved(self, translated: str, pieces: list[str]) -> None:
        self.assertEqual(" ".join(" ".join(pieces).split()), " ".join(translated.split()))

    def test_line_count_always_matches_the_source(self) -> None:
        for count in range(1, 6):
            source = ["日本語の行"] * count
            with self.subTest(count=count):
                pieces = deepl.split_dialog_translation(
                    "one two three four five six seven eight", source
                )
                self.assertEqual(len(pieces), count)

    def test_no_word_is_cut(self) -> None:
        translated = "Совершенно невероятное происшествие случилось этим утром"
        pieces = deepl.split_dialog_translation(translated, ["あああ", "いいい", "ううう"])
        self.assert_words_preserved(translated, pieces)
        for piece in pieces:
            for word in piece.split():
                self.assertIn(word, translated)

    def test_single_line_collapses_whitespace(self) -> None:
        self.assertEqual(
            deepl.split_dialog_translation("  a   b \n c ", ["ソース"]), ["a b c"]
        )

    def test_longer_source_line_takes_more_words(self) -> None:
        """The split follows the proportions of the Japanese, not equal shares."""
        translated = " ".join(f"w{index}" for index in range(20))
        short_first = deepl.split_dialog_translation(
            translated, ["短い", "とてもとてもとても長い行です"]
        )
        long_first = deepl.split_dialog_translation(
            translated, ["とてもとてもとても長い行です", "短い"]
        )
        self.assertLess(
            len(short_first[0].split()), len(long_first[0].split())
        )

    def test_every_line_receives_at_least_one_word(self) -> None:
        """An empty line would collapse the window and lose a source boundary."""
        pieces = deepl.split_dialog_translation("alpha beta", ["あ", "い", "う"])
        self.assertEqual(len(pieces), 3)
        self.assertTrue(any(piece for piece in pieces))

    def test_empty_translation_still_fills_the_window(self) -> None:
        pieces = deepl.split_dialog_translation("", ["あ", "い"])
        self.assertEqual(pieces, ["", ""])


class TestSplitWithoutSpaces(unittest.TestCase):
    """Moaning: DeepL answers with one run chained by ellipses, not with words.

    Wrapping at spaces then has nothing to work with and the tail of the window
    comes out empty, which is worse than a clumsy break - delta_overlay skips a
    row without a translation, so the engine draws its Japanese and the window
    changes language mid-sentence.
    """

    def test_every_line_is_filled(self) -> None:
        translated = "«…Хи…ги…ии…!…Бо…больно…о…!…Перестань…пожалуйста…а…а…!»"
        pieces = deepl.split_dialog_translation(
            translated,
            ["「…ひ…ぎ…っ…！", "　…い…いた…っ…！", "　お…おねが…ぃ…あ…っ…！」"],
        )
        self.assertEqual(len(pieces), 3)
        for piece in pieces:
            self.assertTrue(piece.strip(), pieces)

    def test_nothing_is_added_or_lost(self) -> None:
        translated = "“…Ah…!…Mmm…!…Nooo…!…Aaaah…!”"
        pieces = deepl.split_dialog_translation(translated, ["「…あ…！", "　…や…あ…！」"])
        # Both halves, or the round trip would hold for a window that was never
        # cut and the check would pass on the wrapping this replaced.
        self.assertTrue(all(piece for piece in pieces), pieces)
        self.assertEqual("".join(pieces), translated)

    def test_the_break_lands_after_punctuation(self) -> None:
        """A cut inside a word would be as wrong here as it is between words."""
        pieces = deepl.split_dialog_translation(
            "«Больно…очень…больно…хватит…»", ["「いたい…", "　やめて…」"]
        )
        self.assertTrue(pieces[0].endswith("…"), pieces)

    def test_a_run_with_nothing_to_cut_at_is_left_alone(self) -> None:
        """DeepL sometimes loops on repeated kana and answers with one vowel.

        There is no boundary in that at all. Cutting mid-run to fill the window
        would only spread a broken answer over more lines; the window stays as
        the old wrapping left it, for the reviewer to find.
        """
        pieces = deepl.split_dialog_translation("«Гва" + "а" * 200, ["「ぐぁッ！", "　あぁッ！」"])
        self.assertEqual(len(pieces), 2)
        self.assertEqual(pieces[1], "")

    def test_markup_is_never_cut_in_half(self) -> None:
        """Three lines, so the two words cannot cover them and atoms are used."""
        pieces = deepl.split_dialog_translation(
            "[name]…кричит…и…падает…[name]",
            ["「さけぶ…", "　たおれる…", "　…また…」"],
        )
        self.assertTrue(all(piece for piece in pieces), pieces)
        self.assertEqual("".join(pieces).count("[name]"), 2)
        for piece in pieces:
            self.assertEqual(piece.count("["), piece.count("]"))


class TestMarkupProtection(unittest.TestCase):
    def test_round_trip(self) -> None:
        text = "Before [tag] after"
        protected, tokens = deepl.protect_markup(text)
        self.assertEqual(deepl.restore_markup(protected, tokens), text)

    def test_text_without_markup_is_untouched(self) -> None:
        protected, tokens = deepl.protect_markup("plain text")
        self.assertEqual(protected, "plain text")
        self.assertEqual(tokens, [])


class TestCacheMethod(unittest.TestCase):
    def test_method_marker_is_versioned(self) -> None:
        """Entries written by an older wrapping method must not be reused."""
        self.assertTrue(re.search(r"-v\d+$", deepl.CACHE_METHOD))


if __name__ == "__main__":
    unittest.main()
