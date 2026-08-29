"""Portable contract for the GARbro-backed C# Triangle resource tool.

The tests intentionally exercise the command-level behaviour that a C# port
must retain.  They use a tiny checked-in archive rather than the installed game,
so missing game files cannot silently turn the migration safety net into skips.
"""

from __future__ import annotations

import hashlib
import json
import shutil
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path

from context import FIXTURES_DIR, GOLDEN_DIR, RESOURCE_TOOL, requires_garbro

import imagelib


FIXTURE_DIR = FIXTURES_DIR / "triangle_codec"
GOLDEN_PATH = GOLDEN_DIR / "triangle_codec.json"
ARCHIVE_TOOL = RESOURCE_TOOL


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_golden() -> dict:
    return json.loads(GOLDEN_PATH.read_text(encoding="utf-8"))


def run_tool(*arguments: object, expected_code: int = 0) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        [str(ARCHIVE_TOOL), *(str(value) for value in arguments)],
        capture_output=True,
        text=True,
    )
    if result.returncode != expected_code:
        raise AssertionError(
            f"DeltaResourceTool exited with {result.returncode}, expected {expected_code}\n"
            f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
        )
    return result


def raw_cgf_payloads(path: Path) -> dict[str, tuple[bool, bytes]]:
    """Read only the index boundaries, independently of the implementation."""
    data = path.read_bytes()
    count = struct.unpack_from("<I", data, 0)[0]
    records: list[tuple[str, bool, int]] = []
    for index in range(count):
        start = 4 + index * 32
        name = data[start : start + 28].split(b"\0", 1)[0].decode("cp932")
        value = struct.unpack_from("<I", data, start + 28)[0]
        records.append((name, bool(value & 0x80000000), value & 0x7FFFFFFF))
    payloads: dict[str, tuple[bool, bytes]] = {}
    for index, (name, flagged, offset) in enumerate(records):
        end = records[index + 1][2] if index + 1 < len(records) else len(data)
        payloads[name] = (flagged, data[offset:end])
    return payloads


@requires_garbro
class TestTriangleArchiveContract(unittest.TestCase):
    """Characterises the command-level Triangle archive operations."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.golden = load_golden()
        cls.archive = FIXTURE_DIR / "triangle_fixture.cgf"
        missing = [
            path.name
            for path in (
                cls.archive,
                FIXTURE_DIR / "DUMMY.IAF",
                FIXTURE_DIR / "SDUMMY.IAF",
                FIXTURE_DIR / "NATIVE.IAF",
            )
            if not path.is_file()
        ]
        if missing:
            raise AssertionError(f"Missing Triangle fixtures: {', '.join(missing)}")

    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="triangle-contract-")
        self.work = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_checked_in_sources_are_pinned(self) -> None:
        for name, expected in self.golden["sources"].items():
            with self.subTest(source=name):
                data = (FIXTURE_DIR / name).read_bytes()
                self.assertEqual(len(data), expected["bytes"])
                self.assertEqual(digest(data), expected["sha256"])

    def test_json_list_exposes_the_garbro_index(self) -> None:
        entries = json.loads(run_tool("cgf", "list", self.archive, "--json").stdout)
        raw = raw_cgf_payloads(self.archive)
        self.assertEqual([entry["name"] for entry in entries], list(raw))
        self.assertEqual([entry["type"] for entry in entries], ["image", "image", "image"])
        self.assertEqual(
            [entry["offset"] for entry in entries],
            [struct.unpack_from("<I", self.archive.read_bytes(), 4 + index * 32 + 28)[0] & 0x7FFFFFFF
             for index in range(3)],
        )

    def test_extract_decodes_every_payload_mode_and_writes_manifest(self) -> None:
        output = self.work / "extract"
        run_tool("cgf", "extract", self.archive, output)

        actual_manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(actual_manifest, self.golden["extract_manifest"])
        for name, expected in self.golden["extracted"].items():
            with self.subTest(entry=name):
                data = (output / name).read_bytes()
                self.assertEqual(len(data), expected["bytes"])
                self.assertEqual(digest(data), expected["sha256"])

    def test_extract_previews_preserve_decoded_iaf_pixels(self) -> None:
        output = self.work / "extract"
        run_tool("cgf", "extract", self.archive, output)
        for name, expected in self.golden["previews"].items():
            with self.subTest(preview=name):
                data = (output / name).read_bytes()
                image = imagelib.read_png(data)
                self.assertEqual(
                    (image.width, image.height, image.depth),
                    (expected["width"], expected["height"], expected["depth"]),
                )
                self.assertEqual(image.signature, expected["pixels"])

    def test_native_compressed_iaf_decodes_to_the_recorded_bitmap(self) -> None:
        output = self.work / "native.bmp"
        run_tool("iaf", "unwrap", FIXTURE_DIR / "NATIVE.IAF", output)
        data = output.read_bytes()
        image = imagelib.read_bmp(data)
        expected = self.golden["native_iaf"]
        self.assertEqual(len(data), expected["bmp_bytes"])
        self.assertEqual(digest(data), expected["bmp_sha256"])
        self.assertEqual(
            (image.width, image.height, image.depth),
            (expected["width"], expected["height"], expected["depth"]),
        )
        self.assertEqual(image.signature, expected["pixels"])

    def test_build_localized_replaces_only_the_named_image(self) -> None:
        assets = self.work / "assets"
        assets.mkdir()
        shutil.copyfile(FIXTURE_DIR / "DUMMY.IAF", assets / "DUMMY.jp.IAF")
        shutil.copyfile(FIXTURE_DIR / "SDUMMY.IAF", assets / "DUMMY.en.IAF")

        localized = self.work / "localized.cgf"
        run_tool(
            "cgf", "build-localized", self.archive, assets, localized,
            "--language", "en",
        )
        output = self.work / "localized_extract"
        run_tool("cgf", "extract", localized, output)

        manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(
            [(item["name"], item["flagged"]) for item in manifest["entries"]],
            [(item["name"], item["flagged"]) for item in self.golden["extract_manifest"]["entries"]],
        )

        source_payloads = raw_cgf_payloads(self.archive)
        localized_payloads = raw_cgf_payloads(localized)
        self.assertEqual(list(localized_payloads), list(source_payloads))
        for untouched in ("SDUMMY", "MASK"):
            with self.subTest(raw_payload=untouched):
                self.assertEqual(localized_payloads[untouched], source_payloads[untouched])

        source_output = self.work / "source_extract"
        run_tool("cgf", "extract", self.archive, source_output, "--no-images")
        for untouched in ("SDUMMY.IAF", "MASK.IAF"):
            with self.subTest(untouched=untouched):
                self.assertEqual(
                    (output / untouched).read_bytes(),
                    (source_output / untouched).read_bytes(),
                )
        self.assertEqual(
            imagelib.read_png((output / "DUMMY.png").read_bytes()).signature,
            self.golden["previews"]["SDUMMY.png"]["pixels"],
        )

    def test_extract_localizable_reads_base_cgf_and_loose_iaf_only(self) -> None:
        cg = self.work / "CG"
        assets = self.work / "ui_assets"
        cg.mkdir()
        shutil.copyfile(self.archive, cg / "ST.CGF")
        shutil.copyfile(self.archive, cg / "ST.ru.CGF")
        shutil.copyfile(FIXTURE_DIR / "DUMMY.IAF", cg / "MENU.IAF")
        shutil.copyfile(FIXTURE_DIR / "SDUMMY.IAF", cg / "MENU.en.IAF")

        run_tool("cgf", "extract-localizable", cg, assets)

        self.assertTrue((assets / "ST" / "DUMMY.jp.IAF").is_file())
        self.assertTrue((assets / "ST" / "DUMMY.jp.png").is_file())
        self.assertTrue((assets / "_loose" / "MENU.jp.IAF").is_file())
        self.assertTrue((assets / "_loose" / "MENU.jp.png").is_file())
        self.assertFalse((assets / "ST.ru").exists())
        self.assertFalse((assets / "_loose" / "MENU.en.jp.png").exists())

    def test_build_localized_set_packages_only_explicit_language_images(self) -> None:
        cg = self.work / "CG"
        assets = self.work / "ui_assets"
        previews = self.work / "previews"
        (assets / "ST").mkdir(parents=True)
        (assets / "_loose").mkdir()
        cg.mkdir()
        shutil.copyfile(self.archive, cg / "ST.CGF")
        shutil.copyfile(FIXTURE_DIR / "DUMMY.IAF", cg / "MENU.IAF")
        run_tool("cgf", "extract", self.archive, previews)
        shutil.copyfile(previews / "SDUMMY.png", assets / "ST" / "DUMMY.en.png")
        shutil.copyfile(previews / "SDUMMY.png", assets / "_loose" / "MENU.ru.png")

        run_tool("cgf", "build-localized-set", cg, assets)

        self.assertTrue((cg / "ST.en.CGF").is_file())
        self.assertFalse((cg / "ST.ru.CGF").exists())
        self.assertTrue((cg / "MENU.ru.IAF").is_file())
        self.assertFalse((cg / "MENU.en.IAF").exists())
        self.assertTrue((cg / "ST.jp.CGF").is_file())
        self.assertTrue((cg / "MENU.jp.IAF").is_file())

        localized = self.work / "localized"
        run_tool("cgf", "extract", cg / "ST.en.CGF", localized)
        self.assertEqual(
            imagelib.read_png((localized / "DUMMY.png").read_bytes()).signature,
            self.golden["previews"]["SDUMMY.png"]["pixels"],
        )

    def test_truncated_cgf_is_rejected_without_outputs(self) -> None:
        broken = self.work / "truncated.cgf"
        broken.write_bytes(self.archive.read_bytes()[:50])
        output = self.work / "extract"
        result = run_tool("cgf", "extract", broken, output, expected_code=1)
        self.assertIn("error:", result.stderr)
        self.assertFalse((output / "manifest.json").exists())

    def test_invalid_iaf_is_rejected_without_an_image(self) -> None:
        broken = self.work / "invalid.iaf"
        broken.write_bytes(struct.pack("<I", 0xDEADBEEF) + b"not an image")
        output = self.work / "invalid.bmp"
        result = run_tool("iaf", "unwrap", broken, output, expected_code=1)
        self.assertIn("error:", result.stderr)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
