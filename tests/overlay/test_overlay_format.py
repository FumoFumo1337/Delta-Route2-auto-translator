"""The RKT3 overlay and the half-width codepage it carries.

The Python writer and the C++ parser in winmm.dll have to agree byte for byte;
this file pins the writer's side of that contract. read_overlay is the writer's
own inverse, so a round trip alone would not catch a layout change - the header
is checked field by field as well.
"""

from __future__ import annotations

import io
import struct
import tempfile
import unittest
from collections import Counter
from contextlib import redirect_stderr
from pathlib import Path

from context import TOOLS_ROOT  # noqa: F401  (prepares sys.path)

import delta_overlay as overlay


class TestCodepage(unittest.TestCase):
    def test_pool_avoids_cp932_lead_bytes(self) -> None:
        """A code that starts a CP932 pair would swallow the byte after it.

        Rather than restate the lead-byte ranges, each code is asked to decode
        on its own: a lead byte cannot.
        """
        for code in overlay.code_pool():
            with self.subTest(code=hex(code)):
                try:
                    bytes([code]).decode("cp932")
                except UnicodeDecodeError:
                    self.fail(f"{code:#04x} is a CP932 lead byte")

    def test_pool_excludes_reserved_bytes(self) -> None:
        """NUL terminates, tab and '@' are read by the engine itself."""
        self.assertEqual(overlay.RESERVED_BYTES, frozenset({0x00, 0x09, 0x40}))
        for code in overlay.code_pool():
            self.assertNotIn(code, overlay.RESERVED_BYTES)

    def test_pool_has_no_duplicates(self) -> None:
        pool = overlay.code_pool()
        self.assertEqual(len(pool), len(set(pool)))

    def test_every_mapped_character_gets_a_code(self) -> None:
        char_to_byte, byte_to_char = overlay.build_encoding()
        for character in overlay.MAPPED_CHARACTERS:
            self.assertIn(character, char_to_byte)
        # The two directions must stay consistent for the proxy to decode.
        for character in overlay.MAPPED_CHARACTERS:
            self.assertEqual(byte_to_char[char_to_byte[character]], character)

    def test_ascii_keeps_its_own_codes(self) -> None:
        char_to_byte, _ = overlay.build_encoding()
        for character in "AZaz09 .,:;!?":
            with self.subTest(character=character):
                self.assertEqual(char_to_byte[character], ord(character))


class TestEncodeTranslation(unittest.TestCase):
    def setUp(self) -> None:
        self.char_to_byte, self.byte_to_char = overlay.build_encoding()

    def encode(self, text: str) -> tuple[bytes, Counter]:
        unmapped: Counter = Counter()
        return overlay.encode_translation(text, self.char_to_byte, unmapped), unmapped

    def test_round_trips_russian(self) -> None:
        text = "Привет, мир!"
        encoded, unmapped = self.encode(text)
        self.assertFalse(unmapped)
        self.assertEqual(
            "".join(self.byte_to_char[code] for code in encoded), text
        )

    def test_normalizes_japanese_quotes(self) -> None:
        text = "「текст」 『text』"
        encoded, unmapped = self.encode(text)
        self.assertFalse(unmapped)
        self.assertEqual(
            "".join(self.byte_to_char[code] for code in encoded),
            "«текст» «text»",
        )

    def test_one_byte_per_character(self) -> None:
        """Halving a line's width is the whole point of the private codepage."""
        encoded, _ = self.encode("Привет")
        self.assertEqual(len(encoded), 6)

    def test_unmapped_characters_are_counted_not_dropped(self) -> None:
        encoded, unmapped = self.encode("Ω")
        self.assertEqual(encoded, b"?")
        self.assertEqual(unmapped["Ω"], 1)

    def test_never_emits_a_reserved_byte(self) -> None:
        text = "Привет, мир! «Ёлка» — тест… 123"
        encoded, _ = self.encode(text)
        for code in encoded:
            self.assertNotIn(code, overlay.RESERVED_BYTES)

    def test_unmapped_characters_emit_a_cli_warning(self) -> None:
        errors = io.StringIO()
        with redirect_stderr(errors):
            overlay.warn_unmapped(Counter({"Ω": 2}))
        self.assertEqual(
            errors.getvalue(),
            "WARNING: unsupported characters replaced with '?': 'Ω'x2\n",
        )


class TestOverlayFile(unittest.TestCase):
    ENTRIES = [
        ("行こう".encode("cp932"), "Идём"),
        ("麗佳".encode("cp932"), "Рейка"),
        ("test".encode("cp932"), "тест"),
    ]

    def write(self, directory: Path, standalone=None) -> tuple[Path, dict]:
        output = directory / "delta_overlay.test.bin"
        report = overlay.write_overlay(list(self.ENTRIES), output, standalone)
        return output, report

    def test_round_trip(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output, _ = self.write(Path(directory))
            read_back = overlay.read_overlay(output)
        self.assertEqual([item[0] for item in read_back], [e[0] for e in self.ENTRIES])
        self.assertEqual([item[1] for item in read_back], [e[1] for e in self.ENTRIES])

    def test_header_layout(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output, _ = self.write(Path(directory))
            raw = output.read_bytes()

        self.assertEqual(raw[:4], overlay.OVERLAY_MAGIC)
        self.assertEqual(struct.unpack_from("<I", raw, 4)[0], len(self.ENTRIES))
        # 256 codes, two bytes each, immediately after magic and count.
        self.assertGreaterEqual(len(raw), 8 + 512)

        position = 8 + 512
        for source, translation in self.ENTRIES:
            source_size, translation_size, flags = struct.unpack_from("<III", raw, position)
            position += 12
            self.assertEqual(source_size, len(source))
            self.assertEqual(raw[position : position + source_size], source)
            position += source_size
            self.assertEqual(translation_size, len(translation))
            position += translation_size
            self.assertEqual(flags, 0)
        self.assertEqual(position, len(raw))

    def test_standalone_flag_is_recorded(self) -> None:
        marked = {self.ENTRIES[1][0]}
        with tempfile.TemporaryDirectory() as directory:
            output, report = self.write(Path(directory), marked)
            read_back = overlay.read_overlay(output)
        self.assertEqual(report["standalone"], 1)
        self.assertEqual([item[2] for item in read_back], [False, True, False])

    def test_duplicate_source_is_rejected(self) -> None:
        """One source maps to one translation; two would be a silent coin toss."""
        entries = [(b"same", "one"), (b"same", "two")]
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                overlay.write_overlay(entries, Path(directory) / "out.bin")

    def test_codepage_maps_the_custom_codes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output, _ = self.write(Path(directory))
            raw = output.read_bytes()
        char_to_byte, _ = overlay.build_encoding()
        codepage = raw[8 : 8 + 512]
        for character in overlay.MAPPED_CHARACTERS:
            code = char_to_byte[character]
            stored = struct.unpack_from("<H", codepage, code * 2)[0]
            with self.subTest(character=character):
                self.assertEqual(stored, ord(character))


if __name__ == "__main__":
    unittest.main()
