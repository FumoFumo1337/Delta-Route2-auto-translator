"""The classifier that separates scenario text from binary records.

Every case here was taken from the shipped RSAN.SD, not invented. The engine
interleaves prose with bytecode, and a three-byte record whose first byte lands
in a CP932 lead range decodes to a plausible kanji - which is how 125 binary
records reached the workbook and were paid for at DeepL.
"""

from __future__ import annotations

import unittest

from context import TOOLS_ROOT  # noqa: F401  (prepares sys.path)

import delta_overlay as overlay


# Real lines from the script. Losing any of these would silently drop dialogue.
REAL_LINES = [
    "「山田さんも、面白い話を持ってきてくれたものだ」",
    "「麗佳？麗佳！」",
    "『回想』",
    "（玲？",
    "Ｄｏｌｌ…麗佳…",
    "効果覿面。",
    "ーーー",
    "・",
]

# Real three-byte bytecode records, decoded as CP932.
BINARY_RECORDS = [
    "挿\t",
    "抑\t",
]


class TestLooksLikeScenarioText(unittest.TestCase):
    def test_real_lines_are_kept(self) -> None:
        for line in REAL_LINES:
            with self.subTest(line=line):
                self.assertTrue(overlay.looks_like_scenario_text(line))

    def test_binary_records_are_rejected(self) -> None:
        for record in BINARY_RECORDS:
            with self.subTest(record=record):
                self.assertFalse(overlay.looks_like_scenario_text(record))

    def test_kanji_only_lines_survive_without_kana(self) -> None:
        """Requiring kana alone would have dropped 14 real lines.

        Japanese prose normally cannot be written without kana, but short name
        calls and set phrases can be. They are recognised by being written
        entirely in full-width forms instead.
        """
        for line in ("「麗佳？麗佳！」", "効果覿面。", "（玲？"):
            with self.subTest(line=line):
                self.assertFalse(overlay.has_kana(line))
                self.assertTrue(overlay.looks_like_scenario_text(line))

    def test_punctuation_alone_is_not_scenario_text(self) -> None:
        for text in ("「」", "「", "。", "、", "…………", "ＯＫ"):
            with self.subTest(text=text):
                self.assertFalse(overlay.looks_like_scenario_text(text))

    def test_complete_japanese_quote_frame_requires_a_matching_pair(self) -> None:
        for text in ("「…………」", "『…………』"):
            with self.subTest(text=text):
                self.assertTrue(overlay.has_complete_japanese_quote_frame(text))
        for text in ("「…………", "…………」", "「…………』", "『…………」"):
            with self.subTest(text=text):
                self.assertFalse(overlay.has_complete_japanese_quote_frame(text))

    def test_half_width_katakana_is_not_evidence_of_prose(self) -> None:
        """It never appears in this script but constantly inside binary records.

        Counting it as kana turned 55 binary records back into accepted text.
        """
        self.assertFalse(overlay.has_kana("ﾏﾋｶ"))
        self.assertTrue(overlay.has_kana("マヒカ"))

    def test_a_mixed_line_has_to_be_long_enough(self) -> None:
        """The shortest real line carrying a half-width mark is seven characters,
        the longest binary record three. The threshold sits between them."""
        self.assertFalse(overlay.looks_like_scenario_text("あ!"))
        self.assertTrue(overlay.looks_like_scenario_text("あいうえおかき!"))


class TestFullWidth(unittest.TestCase):
    def test_recognises_japanese_forms(self) -> None:
        for char in "あアー漢、。「」！？　…－":
            with self.subTest(char=char):
                self.assertTrue(overlay.is_full_width(char))

    def test_rejects_ascii_and_half_width(self) -> None:
        for char in "aZ0!\t \x00ﾏ｡":
            with self.subTest(char=repr(char)):
                self.assertFalse(overlay.is_full_width(char))


class TestChunking(unittest.TestCase):
    def test_iter_null_chunks_skips_empty_runs(self) -> None:
        data = b"one\x00\x00two\x00three"
        self.assertEqual(
            list(overlay.iter_null_chunks(data)),
            [(0, b"one"), (5, b"two"), (9, b"three")],
        )

    def test_speaker_command_matches_only_the_plate_form(self) -> None:
        for good in ("N0", "N1", "N12"):
            self.assertIsNotNone(overlay.SPEAKER_COMMAND.fullmatch(good))
        for bad in ("N", "N123", "M", "NA", "0N"):
            self.assertIsNone(overlay.SPEAKER_COMMAND.fullmatch(bad))

    def test_dialog_boundaries_are_m_and_f(self) -> None:
        self.assertEqual(overlay.DIALOG_BOUNDARY_COMMANDS, {"M", "F"})


if __name__ == "__main__":
    unittest.main()
