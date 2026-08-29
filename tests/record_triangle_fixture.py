"""Rebuild the small Triangle CGF fixture used by contract tests.

The native compressed IAF sample is intentionally not regenerated here: it is
an input captured from the game and pinned by hash in the golden file.
"""

from __future__ import annotations

import struct
from pathlib import Path

import context  # noqa: F401 - prepares py/ on sys.path

import reference_triangle_codec as reference


FIXTURE_DIR = Path(__file__).resolve().parent / "fixtures" / "triangle_codec"


def make_bmp(width: int, height: int, rgb_rows: list[list[tuple[int, int, int]]]) -> bytes:
    stride = ((width * 3 + 3) // 4) * 4
    body = bytearray()
    for row in reversed(rgb_rows):
        encoded = b"".join(bytes((blue, green, red)) for red, green, blue in row)
        body.extend(encoded.ljust(stride, b"\0"))
    pixel_offset = 14 + 40
    return (
        struct.pack("<2sIHHI", b"BM", pixel_offset + len(body), 0, 0, pixel_offset)
        + struct.pack(
            "<IiiHHIIiiII", 40, width, height, 1, 24, 0, len(body), 0, 0, 0, 0
        )
        + body
    )


def write_archive(entries: list[tuple[str, bool, str, bytes]], output: Path) -> None:
    payloads = [reference.pack_payload(data, mode) for _, _, mode, data in entries]
    index = bytearray(struct.pack("<I", len(entries)))
    offset = 4 + len(entries) * reference.ENTRY_RECORD
    for (name, flagged, _, _), payload in zip(entries, payloads):
        encoded = name.encode("cp932")
        value = offset | (reference.INDEX_FLAG if flagged else 0)
        index.extend(
            encoded.ljust(reference.NAME_FIELD, b"\0") + struct.pack("<I", value)
        )
        offset += len(payload)
    output.write_bytes(bytes(index) + b"".join(payloads))


def main() -> int:
    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)
    pictures = {
        "DUMMY": make_bmp(
            4,
            3,
            [
                [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255)],
                [(0, 0, 0), (255, 255, 0), (0, 255, 255), (255, 0, 255)],
                [(32, 64, 96), (64, 96, 128), (96, 128, 160), (128, 160, 192)],
            ],
        ),
        "SDUMMY": make_bmp(
            4,
            3,
            [
                [(12, 24, 36)] * 4,
                [(220, 30, 40)] * 4,
                [(50, 180, 70)] * 4,
            ],
        ),
    }
    modes = {"DUMMY": "stored", "SDUMMY": "lzss"}
    for name, bitmap in pictures.items():
        (FIXTURE_DIR / f"{name}.bmp").write_bytes(bitmap)
        (FIXTURE_DIR / f"{name}.IAF").write_bytes(reference.wrap_bmp_bytes(bitmap))
    write_archive(
        [
            ("DUMMY", False, modes["DUMMY"], pictures["DUMMY"]),
            ("SDUMMY", False, modes["SDUMMY"], pictures["SDUMMY"]),
            ("MASK", True, "raw", (FIXTURE_DIR / "MASK.raw").read_bytes()),
        ],
        FIXTURE_DIR / "triangle_fixture.cgf",
    )
    print(f"Rebuilt Triangle fixtures in {FIXTURE_DIR}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
