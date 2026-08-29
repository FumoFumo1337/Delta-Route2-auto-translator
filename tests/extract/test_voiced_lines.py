"""Lines the game speaks, which carry their voice clip in front of the text.

Every byte string here was copied out of the shipped RSAN.SD. A voiced record is
the clip name, then six bytes locating the sample, then the line - and nothing
marks where the six end. Whatever part of them holds no zero therefore arrives
glued to the first character, and the rule that throws away chunks containing a
control byte threw away the line with them. Reika is the only voiced character,
so what went missing was her dialogue: 3208 rows of it.

The recovery cuts at a fixed distance from the clip name rather than looking for
where prose starts, which is also what keeps a locator that happens to decode as
a kanji from being mistaken for a line.
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from context import TOOLS_ROOT  # noqa: F401  (prepares sys.path)

import delta_overlay as overlay


BOUNDARY = b"M\x00"
SPEAKER = b"N2\x00"


def message(text: str) -> bytes:
    """One scenario line as the script stores it."""
    return text.encode("cp932") + b"\x00"


def voiced(name: str, locator: bytes, text: str) -> bytes:
    """The same line with a voice clip: name, six locator bytes, text."""
    assert len(locator) == overlay.VOICE_HEADER_SIZE
    return name.encode("ascii") + b"\x00" + locator + message(text)


def extract(data: bytes) -> list[overlay.TextEntry]:
    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "RSAN.SD"
        path.write_bytes(data)
        return overlay.extract_entries(path)


def texts(data: bytes) -> list[str]:
    return [entry.text for entry in extract(data)]


class TestVoicedLines(unittest.TestCase):
    # 0x0153E0: the line a couple of windows after the second option of the
    # C00006 menu, and the report that started this. Its locator holds no zero
    # at all, so the whole of it shares a chunk with the text.
    LINE = "「はぁ、ン、くぅ…ッ！」"
    LOCATOR = bytes.fromhex("00002c636f02")

    def test_a_voiced_line_is_extracted(self) -> None:
        data = SPEAKER + voiced("vA020428", self.LOCATOR, self.LINE) + BOUNDARY
        self.assertEqual(texts(data), [self.LINE])

    def test_a_voiced_punctuation_only_quote_is_extracted(self) -> None:
        line = "「…………」"
        data = SPEAKER + voiced("vA020428", self.LOCATOR, line) + BOUNDARY
        self.assertEqual(texts(data), [line])

    def test_punctuation_only_quote_requires_a_voice_command(self) -> None:
        line = "「…………」"
        data = SPEAKER + message(line) + BOUNDARY
        self.assertEqual(texts(data), [])

    def test_voiced_punctuation_requires_matching_complete_quotes(self) -> None:
        for line in ("「…………", "…………」", "「…………』", "『…………」"):
            with self.subTest(line=line):
                data = SPEAKER + voiced("vA020428", self.LOCATOR, line) + BOUNDARY
                self.assertEqual(texts(data), [])

    def test_the_clip_name_is_not_a_line(self) -> None:
        data = SPEAKER + voiced("vA020428", self.LOCATOR, self.LINE) + BOUNDARY
        self.assertNotIn("vA020428", texts(data))

    def test_a_locator_that_reads_as_a_kanji_is_not_a_line(self) -> None:
        """0x0019A0, where the locator decodes as 臉 between two zero runs.

        The zeros end the chunk before the text, so this line was extracted all
        along - with the kanji in front of it as a row of its own.
        """
        locator = bytes.fromhex("0000e45f0000")
        line = "「私、公園に行きたい」"
        data = SPEAKER + voiced("vA020002", locator, line) + BOUNDARY
        self.assertEqual(texts(data), [line])

    def test_a_voiced_line_keeps_its_continuation(self) -> None:
        """0x015560, a window whose first half used to be dropped.

        The half that survived begins with an ideographic space and ends on a
        closing quote, so the workbook showed half a sentence as a whole window.
        """
        locator = bytes.fromhex("00006a277102")
        opening = "「いやぁっ！ほどいて！！"
        closing = "　こんな格好やだぁあぁぁぁッ！！」"
        data = SPEAKER + voiced("vA020430", locator, opening) + message(closing) + BOUNDARY
        entries = extract(data)
        self.assertEqual([entry.text for entry in entries], [opening, closing])
        self.assertEqual(
            [entry.dialog_lines[0] for entry in entries],
            ["D00001 1/2", "D00001 2/2"],
        )

    def test_an_unvoiced_line_is_untouched(self) -> None:
        line = "「一体いつまで気丈なままでいられるのかな」"
        data = b"N1\x00" + message(line) + BOUNDARY
        self.assertEqual(texts(data), [line])

    def test_the_speaker_plate_survives_the_clip(self) -> None:
        """The name plate is set before the clip and has to outlive it."""
        data = SPEAKER + voiced("vA020428", self.LOCATOR, self.LINE) + BOUNDARY
        self.assertEqual([entry.speakers for entry in extract(data)], [(2,)])


if __name__ == "__main__":
    unittest.main()
