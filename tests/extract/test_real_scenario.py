"""Golden extraction counts for the shipped scenario.

These describe one build of one title, so they are pinned to the RSAN.SD hash
the pipeline itself enforces and skip on anything else. They are the tests that
would have caught binary bytecode reaching the workbook.

They would not have caught the opposite, and did not: the counts stood at 13868
rows and 8564 windows while 3208 of Reika's voiced lines were being discarded
as binary records. A count that only ever gets compared with itself says nothing
about what is missing - see extract/test_voiced_lines.py.

The numbers are a record of what the extractor currently sees, not a
specification: when one changes, the change has to be justified before the
number is rewritten.
"""

from __future__ import annotations

import unittest
from collections import Counter

from context import (  # noqa: F401  (prepares sys.path)
    requires_pinned_scenario,
    scenario_path,
)

import delta_overlay as overlay


@requires_pinned_scenario
class TestExtractionGolden(unittest.TestCase):
    """Counts for the pinned RSAN.SD.

    A change here is not automatically a failure - it means the extractor now
    sees the script differently, and the new numbers have to be justified before
    they are written down.
    """

    @classmethod
    def setUpClass(cls) -> None:
        cls.entries = overlay.extract_entries(scenario_path())

    def test_row_count(self) -> None:
        # Nine quoted, punctuation-only voiced lines are now recognised by
        # their voice record and complete Japanese quote frame.
        self.assertEqual(len(self.entries), 17085)

    def test_speaker_split(self) -> None:
        named = [e for e in self.entries if any(index for index in e.speakers)]
        narration = [e for e in self.entries if not any(index for index in e.speakers)]
        self.assertEqual(len(named), 9442)
        self.assertEqual(len(narration), 7643)
        self.assertEqual(len(named) + len(narration), len(self.entries))

    def test_window_count_and_shape(self) -> None:
        sizes: Counter = Counter()
        windows = set()
        for entry in self.entries:
            for location in entry.dialog_lines:
                identifier, shape = location.split()
                windows.add(identifier)
                sizes[int(shape.split("/")[1])] += 1
        self.assertEqual(len(windows), 10014)
        self.assertEqual(max(sizes), 3)

    def test_no_binary_records_survive(self) -> None:
        """Every accepted row must look like prose, not like a bytecode record.

        The records that used to get through were three bytes long and carried a
        control or half-width byte after a kanji.
        """
        suspects = [
            entry
            for entry in self.entries
            if len(entry.text) <= 3
            and any(
                ord(char) < 0x20 or 0xFF61 <= ord(char) <= 0xFF9F
                for char in entry.text
            )
        ]
        self.assertEqual([entry.text for entry in suspects], [])

    def test_short_kanji_only_rows_are_kept(self) -> None:
        """Two-byte single-kanji rows are real and must survive.

        They are why the rule cannot simply drop everything shorter than a few
        characters: these carry labels the overlay translates. A binary record
        is the same length, and is told apart by being not entirely full-width.
        """
        texts = {entry.text for entry in self.entries}
        expected = {"敞", "響"}
        self.assertEqual(sorted(expected - texts), [])

    def test_voice_locators_are_not_rows(self) -> None:
        """臉, 障 and 熬 used to be listed above as real lines.

        Each appears once as a row of its own, and that occurrence is the six
        bytes locating a voice clip, decoded as a kanji because two of them
        happen to form one. The game never draws them; reading the voiced
        record by its shape is what tells them from a line.
        """
        texts = {entry.text for entry in self.entries}
        self.assertEqual(sorted({"臉", "障", "熬"} & texts), [])

    def test_short_rows_are_entirely_full_width(self) -> None:
        for entry in self.entries:
            if len(entry.text) <= 3:
                with self.subTest(text=entry.text):
                    self.assertTrue(all(overlay.is_full_width(c) for c in entry.text))

    def test_no_window_repeats_a_line(self) -> None:
        """A window holding one source twice made the translator write a cell
        twice and append two conflicting cache entries. Every such window turned
        out to be contaminated by a binary record."""
        per_window: dict[str, list[bytes]] = {}
        for entry in self.entries:
            for location in entry.dialog_lines:
                per_window.setdefault(location.split()[0], []).append(entry.source)
        repeated = [
            identifier
            for identifier, sources in per_window.items()
            if len(sources) != len(set(sources))
        ]
        self.assertEqual(repeated, [])

    def test_sources_are_unique(self) -> None:
        """Rows are deduplicated by source bytes, which the overlay relies on."""
        sources = [entry.source for entry in self.entries]
        self.assertEqual(len(sources), len(set(sources)))

    def test_every_row_decodes_as_cp932(self) -> None:
        for entry in self.entries:
            with self.subTest(text=entry.text):
                self.assertEqual(entry.source.decode("cp932"), entry.text)
