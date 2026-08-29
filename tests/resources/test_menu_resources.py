"""Portable regression cases for Delta's executable menu resources.

The fixture is synthetic UTF-16LE data rather than a game executable. This
keeps the extraction/runtime contract pinned while its implementation moves
from Python to the C# resource backend.
"""

from __future__ import annotations

import base64
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

from context import TOOLS_ROOT


RESOURCE_TOOL = TOOLS_ROOT / "bin" / "DeltaResourceTool.exe"


@unittest.skipUnless(RESOURCE_TOOL.is_file(), "DeltaResourceTool.exe has not been built")
class TestMenuResources(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.executable = self.directory / "sample.exe"
        strings = (
            "ファイル\t(&F)",
            "Xメッセージ",
            "クイックセーブ",
            "Route web site",
            "ファイル\t(&F)",  # extraction is stable and deduplicated
            "unrelated ASCII text",
            "日本語だがメニューではない",
        )
        self.executable.write_bytes(("\0".join(strings) + "\0").encode("utf-16le"))

    def run_native(self, *arguments: object) -> None:
        result = subprocess.run(
            [str(RESOURCE_TOOL), "menu", *(str(value) for value in arguments)],
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_extract_filters_normalizes_and_deduplicates(self) -> None:
        catalog = self.directory / "delta_menu.json"
        self.run_native("extract", self.executable, catalog)
        self.assertEqual(
            [item["source"] for item in json.loads(
                catalog.read_text(encoding="utf-8-sig"))["entries"]],
            ["Route web site", "クイックセーブ", "ファイル", "メッセージ"],
        )

    def test_reextract_preserves_translations_and_custom_fields(self) -> None:
        catalog = self.directory / "delta_menu.json"
        old = {
            "format": 1,
            "entries": [
                {
                    "source": "ファイル",
                    "ru": "Файл",
                    "en": "File",
                    "comment": "manual",
                },
                {"source": "исчезнувшая строка", "ru": "old", "en": "old"},
            ],
        }
        catalog.write_text(json.dumps(old, ensure_ascii=False), encoding="utf-8-sig")
        self.run_native("extract", self.executable, catalog)
        value = json.loads(catalog.read_text(encoding="utf-8"))
        entries = {item["source"]: item for item in value["entries"]}
        self.assertEqual(entries["ファイル"]["ru"], "Файл")
        self.assertEqual(entries["ファイル"]["comment"], "manual")
        self.assertNotIn("исчезнувшая строка", entries)
        self.assertEqual(entries["メッセージ"]["en"], "")

    def test_runtime_is_exact_base64_tsv_and_skips_empty_values(self) -> None:
        output = self.directory / "delta_menu.ru.tsv"
        catalog = self.directory / "catalog.json"
        catalog.write_text(json.dumps({"format": 1, "entries": [
            {"source": "ファイル", "ru": "Файл", "en": "File"},
            {"source": "メッセージ", "ru": "", "en": "Messages"},
        ]}, ensure_ascii=False), encoding="utf-8-sig")
        self.run_native("runtime", catalog, output, "--target-lang", "RU")
        source = base64.b64encode("ファイル".encode("utf-8")).decode("ascii")
        translated = base64.b64encode("Файл".encode("utf-8")).decode("ascii")
        self.assertEqual(
            output.read_bytes(), f"{source}\t{translated}{os.linesep}".encode()
        )
if __name__ == "__main__":
    unittest.main()
