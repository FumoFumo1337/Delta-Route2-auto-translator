"""Golden tests for the picture-producing half of the toolset.

This is the safety net for moving the GARbro and System.Drawing work out of
Python. Everything here compares against tests/golden/, recorded by
tests/record_golden.py, and the comparison is deliberately made twice:

    pixels  what the picture is. A port must reproduce this exactly, whichever
            imaging stack it uses.
    bytes   what the encoder wrote. Swapping WPF for System.Drawing changes
            these for an unchanged picture, so a failure here with the pixel
            tests still passing means "the encoder changed", not "the artwork
            broke" - and the reference is then re-recorded on purpose.

The reference is decoded by tests/imagelib.py, which uses nothing but zlib and
struct. Checking .NET output with .NET would have compared the port against
itself and proved nothing.
"""

from __future__ import annotations

import json
import struct
import subprocess
import tempfile
import unittest
import zlib
from pathlib import Path

from context import (
    FIXTURES_DIR,
    GOLDEN_DIR,
    RESOURCE_TOOL,
    requires_garbro,
    requires_pinned_scenario,
)

import imagelib
import record_golden
import reference_triangle_codec as reference_archive


IAF_FIXTURE_DIR = FIXTURES_DIR / "triangle_codec"


def load_reference(name: str) -> dict | None:
    path = GOLDEN_DIR / name
    if not path.is_file():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def make_png(width: int, height: int, rows: list[bytes], color_type: int = 2) -> bytes:
    """A minimal PNG, filter 0 throughout, for testing the reader itself."""
    raw = b"".join(b"\x00" + row for row in rows)
    chunks = [
        (b"IHDR", struct.pack(">IIBBBBB", width, height, 8, color_type, 0, 0, 0)),
        (b"IDAT", zlib.compress(raw)),
        (b"IEND", b""),
    ]
    out = bytearray(b"\x89PNG\r\n\x1a\n")
    for kind, body in chunks:
        out += struct.pack(">I", len(body)) + kind + body
        out += struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    return bytes(out)


def make_bmp(width: int, height: int, rows: list[bytes]) -> bytes:
    """A minimal 24 bit bottom-up BMP; rows are given top first, as BGR."""
    stride = ((width * 24 + 31) // 32) * 4
    body = bytearray()
    for row in reversed(rows):
        body += row.ljust(stride, b"\x00")
    start = 14 + 40
    header = struct.pack("<2sIHHI", b"BM", start + len(body), 0, 0, start)
    info = struct.pack("<IiiHHIIiiII", 40, width, height, 1, 24, 0, len(body), 0, 0, 0, 0)
    return bytes(header + info + body)


class TestImageLib(unittest.TestCase):
    """The reader is new code that everything else trusts, so it is checked
    against images built here byte by byte rather than only against .NET."""

    def test_png_truecolour(self) -> None:
        rows = [b"\xff\x00\x00\x00\xff\x00", b"\x00\x00\xff\xff\xff\xff"]
        image = imagelib.read_png(make_png(2, 2, rows))
        self.assertEqual((image.width, image.height, image.depth), (2, 2, 24))
        self.assertEqual(image.pixel(0, 0), (255, 0, 0, 255))
        self.assertEqual(image.pixel(1, 0), (0, 255, 0, 255))
        self.assertEqual(image.pixel(0, 1), (0, 0, 255, 255))

    def test_png_filters_are_undone(self) -> None:
        """The Sub filter stores differences, so a flat row decodes to a
        gradient unless the filter is actually applied."""
        raw = b"\x01" + b"\x10\x20\x30" + b"\x01\x01\x01"
        chunks = [
            (b"IHDR", struct.pack(">IIBBBBB", 2, 1, 8, 2, 0, 0, 0)),
            (b"IDAT", zlib.compress(raw)),
            (b"IEND", b""),
        ]
        out = bytearray(b"\x89PNG\r\n\x1a\n")
        for kind, body in chunks:
            out += struct.pack(">I", len(body)) + kind + body
            out += struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
        image = imagelib.read_png(bytes(out))
        self.assertEqual(image.pixel(0, 0), (0x10, 0x20, 0x30, 255))
        self.assertEqual(image.pixel(1, 0), (0x11, 0x21, 0x31, 255))

    def test_bmp_rows_run_bottom_up(self) -> None:
        """A reader that ignores this returns a vertically mirrored picture,
        which no hash comparison would explain."""
        top = b"\x00\x00\xff"  # BGR red
        bottom = b"\xff\x00\x00"  # BGR blue
        image = imagelib.read_bmp(make_bmp(1, 2, [top, bottom]))
        self.assertEqual(image.pixel(0, 0), (255, 0, 0, 255))
        self.assertEqual(image.pixel(0, 1), (0, 0, 255, 255))

    def test_bmp_row_padding_is_skipped(self) -> None:
        """Rows are padded to four bytes; counting the padding as pixels
        shears the image one pixel further left on every row."""
        image = imagelib.read_bmp(
            make_bmp(1, 2, [b"\x01\x02\x03", b"\x04\x05\x06"])
        )
        self.assertEqual(image.pixel(0, 0), (3, 2, 1, 255))
        self.assertEqual(image.pixel(0, 1), (6, 5, 4, 255))

    def test_same_picture_at_two_depths_has_one_signature(self) -> None:
        """This is what lets the reference survive an encoder change."""
        png = imagelib.read_png(make_png(2, 1, [b"\x0a\x0b\x0c\x0d\x0e\x0f"]))
        bmp = imagelib.read_bmp(make_bmp(2, 1, [b"\x0c\x0b\x0a\x0f\x0e\x0d"]))
        self.assertEqual(png.signature, bmp.signature)
        self.assertNotEqual(png.depth, 0)

    def test_stored_iaf_unwraps_to_its_bitmap(self) -> None:
        bitmap = make_bmp(1, 1, [b"\x01\x02\x03"])
        self.assertEqual(
            imagelib.unwrap_stored_iaf(reference_archive.wrap_bmp_bytes(bitmap)), bitmap
        )

    def test_compressed_iaf_is_not_mistaken_for_a_stored_one(self) -> None:
        """The archive's own IAFs must fall through to GARbro rather than be
        misread as our uncompressed wrapper."""
        self.assertIsNone(imagelib.unwrap_stored_iaf(b"\x00" * 8 + b"\x36\x05\x00\x80" + b"x" * 40))
        self.assertIsNone(imagelib.unwrap_stored_iaf(b""))


@requires_garbro
class TestIafDecode(unittest.TestCase):
    """Characterises the native IAF reader over checked-in samples."""

    @classmethod
    def setUpClass(cls) -> None:
        reference = load_reference("iaf_decode.json")
        if reference is None:
            raise unittest.SkipTest("golden/iaf_decode.json has not been recorded")
        cls.reference = reference["files"]

    def setUp(self) -> None:
        self.archive = reference_archive
        self.temporary = tempfile.TemporaryDirectory()
        self.output = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def decode(self, source: Path, suffix: str = ".bmp") -> bytes:
        output = self.output / (source.stem + suffix)
        result = subprocess.run(
            [str(RESOURCE_TOOL), "iaf", "unwrap", str(source), str(output)],
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return output.read_bytes()

    def test_sources_are_the_recorded_ones(self) -> None:
        """Without this, a changed input would be reported as a decoder bug."""
        for name, expected in sorted(self.reference.items()):
            with self.subTest(image=name):
                data = (IAF_FIXTURE_DIR / name).read_bytes()
                self.assertEqual(len(data), expected["source_bytes"])
                self.assertEqual(record_golden.digest(data), expected["source_sha256"])

    def test_decoded_pixels_match(self) -> None:
        for name, expected in sorted(self.reference.items()):
            with self.subTest(image=name):
                bitmap = self.decode(IAF_FIXTURE_DIR / name)
                image = imagelib.read_bmp(bitmap)
                self.assertEqual(
                    (image.width, image.height, image.depth),
                    (expected["width"], expected["height"], expected["depth"]),
                )
                self.assertEqual(image.signature, expected["pixels"])

    def test_decoded_bytes_match(self) -> None:
        """Pins the BMP encoder as well as the picture; see TestUiAssetBytes."""
        differing = []
        for name, expected in sorted(self.reference.items()):
            bitmap = self.decode(IAF_FIXTURE_DIR / name)
            if record_golden.digest(bitmap) != expected["bmp_sha256"]:
                differing.append(name)
        self.assertEqual(differing, [])

    def test_wrapping_a_decoded_bitmap_reads_back_unchanged(self) -> None:
        """The writer and the reader have to agree: what wrap_bmp_bytes emits
        must decode to the picture it was given. This is the round trip the
        localized UI depends on, checked here on local IAF samples."""
        for name in sorted(self.reference)[:4]:
            with self.subTest(image=name):
                bitmap = self.decode(IAF_FIXTURE_DIR / name)
                wrapped = self.archive.wrap_bmp_bytes(bitmap)
                self.assertEqual(imagelib.unwrap_stored_iaf(wrapped), bitmap)
                wrapped_path = self.output / (Path(name).stem + ".roundtrip.iaf")
                wrapped_path.write_bytes(wrapped)
                again = self.decode(
                    wrapped_path, ".roundtrip.bmp"
                )
                self.assertEqual(
                    imagelib.read_bmp(again).signature,
                    imagelib.read_bmp(bitmap).signature,
                )


@requires_pinned_scenario
@requires_garbro
class TestCgfIndexThroughGarbro(unittest.TestCase):
    """GARbro's view of the archive, which drives what extract writes out.

    The entry type in particular decides whether an entry is treated as an
    image, so a port that gets it wrong would write .bin files with no preview
    and no error.
    """

    @classmethod
    def setUpClass(cls) -> None:
        reference = load_reference("cgf_index.json")
        if reference is None:
            raise unittest.SkipTest("golden/cgf_index.json has not been recorded")
        cls.reference = reference

    def setUp(self) -> None:
        assert GAME_DIR is not None
        self.path = GAME_DIR / self.reference["archive"]
        if not self.path.is_file():
            self.skipTest(f"{self.reference['archive']} is not present")
        self.archive = reference_archive

    def entries(self) -> list[dict]:
        result = subprocess.run(
            [str(RESOURCE_TOOL), "cgf", "list", str(self.path), "--json"],
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return json.loads(result.stdout)

    def test_archive_is_the_recorded_one(self) -> None:
        self.assertEqual(
            record_golden.digest(self.path.read_bytes()), self.reference["archive_sha256"]
        )

    def test_index_matches(self) -> None:
        self.assertEqual(self.entries(), self.reference["entries"])

    def test_garbro_and_the_local_reader_agree(self) -> None:
        """The independent reference keeps an index reader for compression flags
        GARbro does not expose. The two must describe the same entries, because
        extract pairs them up by offset."""
        local = self.archive.read_cgf_index(self.path.read_bytes())
        self.assertEqual(
            [(item.name, item.offset) for item in local],
            [(entry["name"], entry["offset"]) for entry in self.entries()],
        )


if __name__ == "__main__":
    unittest.main()
