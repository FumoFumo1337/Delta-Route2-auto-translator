"""LZSS, RLE and the CGF/MHU payload container.

These are the independent reference codecs that need no GARbro and no .NET, so
they run everywhere. They are also the parts a port to C# would have to
reproduce byte for byte, which is why the round trips are exhaustive rather
than illustrative.
"""

from __future__ import annotations

import random
import unittest

from context import TOOLS_ROOT  # noqa: F401  (prepares sys.path)

import reference_triangle_codec as archive


SAMPLES = {
    "empty": b"",
    "single": b"A",
    "short": b"hello",
    "runs": b"\x00" * 300 + b"\xff" * 17 + b"\x41" * 4096,
    "no_repeats": bytes(range(256)),
    "text": (b"The quick brown fox jumps over the lazy dog. " * 40),
    "alternating": b"\xde\xad" * 500,
    "sparse": b"\x00" * 5000 + b"payload" + b"\x00" * 5000,
    "binary": bytes((index * 37 + 11) & 0xFF for index in range(3000)),
}


def random_blob(seed: int, size: int) -> bytes:
    generator = random.Random(seed)
    # Mixed entropy: a purely random blob never exercises the match path.
    out = bytearray()
    while len(out) < size:
        if generator.random() < 0.5:
            out.extend(generator.randbytes(generator.randint(1, 40)))
        else:
            out.extend(bytes([generator.randrange(256)]) * generator.randint(1, 60))
    return bytes(out[:size])


class TestLzss(unittest.TestCase):
    def test_round_trip_samples(self) -> None:
        for name, data in SAMPLES.items():
            with self.subTest(sample=name):
                packed = archive.compress_lzss(data)
                self.assertEqual(archive.decompress_lzss(packed, len(data)), data)

    def test_round_trip_random(self) -> None:
        for seed in range(12):
            data = random_blob(seed, 2000 + seed * 500)
            with self.subTest(seed=seed):
                packed = archive.compress_lzss(data)
                self.assertEqual(archive.decompress_lzss(packed, len(data)), data)

    def test_compresses_repetitive_data(self) -> None:
        """A ring-buffer coder that does not shrink a long run is broken."""
        data = b"\x5a" * 8000
        self.assertLess(len(archive.compress_lzss(data)), len(data) // 4)

    def test_decompress_stops_at_the_first_match_past_the_size(self) -> None:
        """The size argument is a stop condition, not a cut.

        The loop checks the length before decoding a token, so a match that
        straddles the limit is emitted whole and the result can run up to one
        maximum match past it. Callers pass the true decoded size, where this
        never shows; a port has to reproduce it anyway.
        """
        data = b"abcdefgh" * 64
        packed = archive.compress_lzss(data)
        decoded = archive.decompress_lzss(packed, 100)
        self.assertGreaterEqual(len(decoded), 100)
        self.assertLessEqual(len(decoded), 100 + archive.WINDOW)
        self.assertEqual(decoded[:100], data[:100])

    def test_match_reaching_the_write_cursor(self) -> None:
        """Regression: the encoder must model the decompressor's ring writes.

        Four bytes are enough. The encoder matched d6 00 00 against the ring at
        the write cursor, where the two zeros were merely the untouched initial
        ring. While copying, the decompressor writes each byte back into the
        ring, so it re-read its own output and produced d6 d6 d6 d6 instead.
        Every byte of the input has to survive a round trip regardless of how
        close the match sits to the cursor.
        """
        data = bytes.fromhex("d6d60000")
        self.assertEqual(
            archive.decompress_lzss(archive.compress_lzss(data), len(data)), data
        )

    def test_self_referential_run(self) -> None:
        """A run right after a literal is the overlapping case at its purest."""
        for filler in (b"\x00", b"\xa5", b"\xff"):
            data = b"\x11" + filler * 40
            with self.subTest(filler=filler):
                self.assertEqual(
                    archive.decompress_lzss(archive.compress_lzss(data), len(data)),
                    data,
                )


class TestRle(unittest.TestCase):
    def test_round_trip_samples(self) -> None:
        for name, data in SAMPLES.items():
            with self.subTest(sample=name):
                packed = archive.compress_rle(data)
                self.assertEqual(archive.decompress_rle(packed, len(data)), data)

    def test_round_trip_random(self) -> None:
        for seed in range(12):
            data = random_blob(100 + seed, 1500 + seed * 300)
            with self.subTest(seed=seed):
                packed = archive.compress_rle(data)
                self.assertEqual(archive.decompress_rle(packed, len(data)), data)

    def test_runs_longer_than_a_byte_counter(self) -> None:
        """255 is the largest count the format can express, so 300 must split."""
        data = b"\x7f" * 300
        packed = archive.compress_rle(data)
        self.assertEqual(archive.decompress_rle(packed, len(data)), data)


class TestPayloadContainer(unittest.TestCase):
    def test_round_trip_each_mode(self) -> None:
        for mode in ("stored", "rle", "lzss"):
            for name, data in SAMPLES.items():
                if not data:
                    continue  # see test_empty_payload_is_not_round_tripped
                with self.subTest(mode=mode, sample=name):
                    blob = archive.pack_payload(data, mode)
                    decoded, seen = archive.unpack_payload(blob)
                    self.assertEqual(decoded, data)
                    self.assertEqual(seen, mode)

    def test_unknown_mode_is_rejected(self) -> None:
        with self.assertRaises(ValueError):
            archive.pack_payload(b"data", "deflate")

    def test_raw_mode_passes_bytes_through(self) -> None:
        self.assertEqual(archive.pack_payload(b"abc", "raw"), b"abc")

    def test_short_blob_reads_as_raw(self) -> None:
        """A container needs an 8-byte header; anything shorter cannot be one."""
        decoded, mode = archive.unpack_payload(b"abc")
        self.assertEqual((decoded, mode), (b"abc", "raw"))

    def test_empty_payload_is_not_round_tripped(self) -> None:
        """Documents a real limitation rather than asserting it is correct.

        Packing empty data produces an all-zero header, which unpack_payload
        treats as the "not a container" marker and hands back verbatim. No
        archive entry in the shipped game is empty, so nothing hits this, but a
        port must either match the quirk or handle the case deliberately.
        """
        blob = archive.pack_payload(b"", "lzss")
        decoded, mode = archive.unpack_payload(blob)
        self.assertEqual(mode, "raw")
        self.assertNotEqual(decoded, b"")


class TestPayloadFlags(unittest.TestCase):
    def test_flag_bits_do_not_overlap_the_size_field(self) -> None:
        self.assertEqual(archive.RLE_FLAG & archive.SIZE_MASK, 0)
        self.assertEqual(archive.STORED_FLAG & archive.SIZE_MASK, 0)
        self.assertEqual(archive.RLE_FLAG & archive.STORED_FLAG, 0)

    def test_size_mask_covers_every_realistic_length(self) -> None:
        self.assertGreater(archive.SIZE_MASK, 1 << 29)


if __name__ == "__main__":
    unittest.main()
