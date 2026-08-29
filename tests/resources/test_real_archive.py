"""Round trips against the archives the game actually ships.

The compressor is one of the parts a C# port has to reproduce byte for byte,
and the payloads that exposed its overlapping-match bug live in these files
rather than in any fixture small enough to check in. They skip on a machine
without a copy of the VN.
"""

from __future__ import annotations

import unittest

from context import GAME_DIR, requires_game  # noqa: F401  (prepares sys.path)

import reference_triangle_codec as archive


@requires_game
class TestRealArchiveCodecs(unittest.TestCase):
    """Round-trips real compressed payloads.

    The compressor is slow, so only the smaller entries run; they are the ones
    that exposed the overlapping-match bug in the first place.
    """

    MAX_BYTES = 8000

    def payloads(self, limit: int):
        assert GAME_DIR is not None
        found = []
        for name in ("MS.MHU", "INSTALL.MHU"):
            path = GAME_DIR / name
            if path.is_file():
                try:
                    payload = path.read_bytes()
                except (OSError, PermissionError):
                    continue
                decoded, mode = archive.unpack_payload(b"\0\0\0\0" + payload)
                if len(decoded) <= self.MAX_BYTES:
                    found.append((name, mode, decoded))

        cgf = GAME_DIR / "CG" / "ST.jp.CGF"
        if cgf.is_file():
            data = cgf.read_bytes()
            entries = []
            for entry in archive.read_cgf_index(data):
                decoded, mode = archive.unpack_payload(
                    data[entry.offset : entry.offset + entry.size]
                )
                if mode != "raw" and len(decoded) <= self.MAX_BYTES:
                    entries.append((len(decoded), entry.name, mode, decoded))
            entries.sort(key=lambda item: item[0])
            found.extend((name, mode, decoded) for _, name, mode, decoded in entries)
        return found[:limit]

    def test_round_trip_real_payloads(self) -> None:
        samples = self.payloads(8)
        if not samples:
            self.skipTest("no small compressed payloads found")
        for name, mode, decoded in samples:
            with self.subTest(entry=name, mode=mode):
                repacked = archive.pack_payload(decoded, mode)
                again, seen = archive.unpack_payload(repacked)
                self.assertEqual(seen, mode)
                self.assertEqual(again, decoded)

    def test_index_is_consistent(self) -> None:
        assert GAME_DIR is not None
        cgf = GAME_DIR / "CG" / "ST.jp.CGF"
        if not cgf.is_file():
            self.skipTest("ST.jp.CGF not present")
        data = cgf.read_bytes()
        entries = archive.read_cgf_index(data)
        self.assertTrue(entries)
        for entry in entries:
            with self.subTest(entry=entry.name):
                self.assertGreater(entry.size, 0)
                self.assertLessEqual(entry.offset + entry.size, len(data))
                self.assertLess(len(entry.name.encode("cp932")), archive.NAME_FIELD)
