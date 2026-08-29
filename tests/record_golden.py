"""Records the image reference the golden tests compare against.

This is a tool, not a test. Run it when a change to the drawing code is meant
to change the pictures, and commit the resulting JSON as the new reference:

    python tests\\record_golden.py

The reference holds two kinds of fingerprint per file. The exact bytes pin
today's encoders; the decoded pixels pin the picture itself. The split is the
point of the whole exercise: a port that swaps WPF for System.Drawing will
legitimately produce different PNG bytes for an identical image, and only the
pixel fingerprint can tell that apart from a drawing regression.

Recording these IAF and CGF references needs GARbro and .NET.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

from context import FIXTURES_DIR, GAME_DIR, RESOURCE_TOOL, TOOLS_ROOT

import imagelib

GOLDEN_DIR = Path(__file__).resolve().parent / "golden"

# Small checked-in samples cover stored, LZSS and native GARbro IAF variants.
# Keeping them local makes the decoder contract portable between checkouts.
IAF_SAMPLE = [
    "DUMMY.IAF",
    "SDUMMY.IAF",
    "NATIVE.IAF",
]
IAF_FIXTURE_DIR = FIXTURES_DIR / "triangle_codec"


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def resource_tool(*arguments: object) -> str:
    result = subprocess.run(
        [str(RESOURCE_TOOL), *(str(value) for value in arguments)],
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if result.returncode != 0:
        raise SystemExit(result.stderr.strip() or result.stdout.strip())
    return result.stdout


def record_iaf() -> dict[str, Any]:
    files = {}
    with tempfile.TemporaryDirectory(prefix="delta-golden-iaf-") as temporary:
        for index, name in enumerate(IAF_SAMPLE):
            path = IAF_FIXTURE_DIR / name
            if not path.is_file():
                print(f"  skipping {name}, not present")
                continue
            output = Path(temporary) / f"{index}.bmp"
            resource_tool("iaf", "unwrap", path, output)
            source = path.read_bytes()
            bitmap = output.read_bytes()
            image = imagelib.read_bmp(bitmap)
            files[name] = {
                "source_bytes": len(source),
                "source_sha256": digest(source),
                "bmp_bytes": len(bitmap),
                "bmp_sha256": digest(bitmap),
                "width": image.width,
                "height": image.height,
                "depth": image.depth,
                "pixels": image.signature,
            }
    return {
        "description": "DeltaResourceTool IAF decoding over checked-in samples.",
        "files": files,
    }


def record_cgf() -> dict[str, Any]:
    if GAME_DIR is None:
        raise SystemExit("No game folder with RSAN.SD was found.")
    archive_path = GAME_DIR / "CG" / "ST.jp.CGF"
    entries = json.loads(resource_tool("cgf", "list", archive_path, "--json"))

    return {
        "description": "GARbro's view of the archive index: names, sizes and types.",
        "archive": "CG/ST.jp.CGF",
        "archive_sha256": digest(archive_path.read_bytes()),
        "entries": entries,
    }


def write(name: str, payload: dict[str, Any]) -> None:
    GOLDEN_DIR.mkdir(parents=True, exist_ok=True)
    path = GOLDEN_DIR / name
    path.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=False) + "\n",
        encoding="utf-8",
    )
    print(f"  wrote {path.relative_to(TOOLS_ROOT)}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--section",
        choices=("iaf", "cgf", "all"),
        default="all",
        help="Record only one part of the reference.",
    )
    args = parser.parse_args()

    if args.section in ("iaf", "all"):
        print("IAF decode through GARbro")
        write("iaf_decode.json", record_iaf())

    if args.section in ("cgf", "all"):
        print("CGF index through GARbro")
        write("cgf_index.json", record_cgf())

    return 0


if __name__ == "__main__":
    sys.exit(main())
