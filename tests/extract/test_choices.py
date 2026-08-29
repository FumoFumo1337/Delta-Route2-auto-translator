"""Finding the menus the reader picks from, and telling them apart from the rest.

A choice reads differently from narration: an option is an action the reader
takes, a message is a fact the reader is told. Translating one as the other is
not a typo the reviewer can spot in the workbook, because nothing in a row says
which it is - hence the C series, and hence these tests.

The detector works on the frame around opcode 0x49 rather than on the opcode
itself. The frame is closed on both sides and states its option count twice
whenever the scenario acts on the answer, so the cases below are mostly about
what happens when one of those statements disagrees with another.

What the label then costs downstream is tested beside the module that pays it:
translate/test_choice_jobs.py and proofread/test_choice_rules.py.
"""

from __future__ import annotations

import struct
import tempfile
import unittest
from pathlib import Path

from context import (  # noqa: F401  (prepares sys.path)
    requires_pinned_scenario,
    scenario_path,
)

import delta_overlay as overlay


BOUNDARY = b"M\x00"


def message(text: str) -> bytes:
    """One scenario line as the script stores it."""
    return text.encode("cp932") + b"\x00"


def menu(options: list[str], targets: tuple[int, ...] = ()) -> bytes:
    """A 0x49 frame, optionally followed by the jump table of a real choice."""
    frame = overlay.CHOICE_OPEN + bytes((len(options), 0))
    for option in options:
        frame += option.encode("cp932") + b"\x00"
    frame += overlay.CHOICE_CLOSE
    if targets:
        frame += overlay.CHOICE_BRANCH + bytes((len(targets), 0))
        frame += b"".join(struct.pack("<I", target) for target in targets)
    return frame


def labels(data: bytes) -> dict[str, list[str]]:
    """Runs the extractor over a synthetic script, keyed by window name."""
    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "RSAN.SD"
        path.write_bytes(data)
        entries = overlay.extract_entries(path)
    windows: dict[str, list[str]] = {}
    for entry in entries:
        for location in entry.dialog_lines:
            windows.setdefault(location.split()[0], []).append(entry.text)
    return windows


class TestFrameReading(unittest.TestCase):
    """read_choice_frame accepts a complete frame and nothing less."""

    OPTIONS = ["「はい」", "「いいえ」"]

    def test_a_complete_frame_is_read_with_its_option_offsets(self) -> None:
        data = menu(self.OPTIONS)
        block = overlay.read_choice_frame(data, 0)
        self.assertIsNotNone(block)
        assert block is not None
        self.assertEqual(
            [text.decode("cp932") for _, text in block.options], self.OPTIONS
        )
        first, second = (offset for offset, _ in block.options)
        self.assertEqual(data[first:first + 8].decode("cp932"), self.OPTIONS[0])
        self.assertGreater(second, first)

    def test_a_frame_without_a_jump_table_does_not_branch(self) -> None:
        block = overlay.read_choice_frame(menu(self.OPTIONS), 0)
        assert block is not None
        self.assertEqual(block.targets, ())
        self.assertFalse(block.branches)

    def test_a_frame_with_a_jump_table_branches(self) -> None:
        block = overlay.read_choice_frame(menu(self.OPTIONS, (4, 8)), 0)
        assert block is not None
        self.assertEqual(block.targets, (4, 8))
        self.assertTrue(block.branches)

    def test_the_open_marker_is_required(self) -> None:
        data = b"\x00" * 4 + menu(self.OPTIONS)
        self.assertIsNone(overlay.read_choice_frame(data, 0))
        self.assertIsNotNone(overlay.read_choice_frame(data, 4))

    def test_a_count_larger_than_the_strings_is_rejected(self) -> None:
        """The header claims three options and the terminator arrives after two.

        This is the check that a lone 0x49 byte cannot pass: the count has to be
        spent exactly, landing the cursor on the terminator.
        """
        data = bytearray(menu(self.OPTIONS))
        data[len(overlay.CHOICE_OPEN)] = 3
        self.assertIsNone(overlay.read_choice_frame(bytes(data), 0))

    def test_a_count_smaller_than_the_strings_is_rejected(self) -> None:
        data = bytearray(menu(self.OPTIONS))
        data[len(overlay.CHOICE_OPEN)] = 1
        self.assertIsNone(overlay.read_choice_frame(bytes(data), 0))

    def test_a_missing_terminator_is_rejected(self) -> None:
        data = menu(self.OPTIONS).replace(overlay.CHOICE_CLOSE, b"")
        self.assertIsNone(overlay.read_choice_frame(data, 0))

    def test_a_truncated_frame_is_rejected(self) -> None:
        full = menu(self.OPTIONS)
        for length in range(len(overlay.CHOICE_OPEN), len(full)):
            with self.subTest(length=length):
                self.assertIsNone(overlay.read_choice_frame(full[:length], 0))

    def test_zero_options_are_rejected(self) -> None:
        data = bytearray(menu(self.OPTIONS))
        data[len(overlay.CHOICE_OPEN)] = 0
        self.assertIsNone(overlay.read_choice_frame(bytes(data), 0))

    def test_an_implausible_option_count_is_rejected(self) -> None:
        data = bytearray(menu(self.OPTIONS))
        data[len(overlay.CHOICE_OPEN)] = overlay.CHOICE_MAX_OPTIONS + 1
        self.assertIsNone(overlay.read_choice_frame(bytes(data), 0))

    def test_the_count_high_byte_must_be_zero(self) -> None:
        data = bytearray(menu(self.OPTIONS))
        data[len(overlay.CHOICE_OPEN) + 1] = 1
        self.assertIsNone(overlay.read_choice_frame(bytes(data), 0))

    def test_an_empty_option_is_rejected(self) -> None:
        self.assertIsNone(overlay.read_choice_frame(menu(["「はい」", ""]), 0))

    def test_an_option_holding_a_control_byte_is_rejected(self) -> None:
        """Bytecode, not text. The extractor rejects such a chunk too, so a frame
        built out of them would produce a window with no rows in it."""
        data = menu(["「はい」", "XX"]).replace(b"XX", b"\x01\x02")
        self.assertIsNone(overlay.read_choice_frame(data, 0))

    def test_an_option_that_is_not_cp932_is_rejected(self) -> None:
        data = menu(["「はい」", "XX"]).replace(b"XX", b"\x82\xff")
        self.assertIsNone(overlay.read_choice_frame(data, 0))

    def test_an_overlong_option_is_rejected(self) -> None:
        data = menu(["「はい」", "A" * (overlay.CHOICE_MAX_OPTION_BYTES + 1)])
        self.assertIsNone(overlay.read_choice_frame(data, 0))


class TestBranchTableAgreement(unittest.TestCase):
    """A jump table only confirms a choice when it agrees with the header.

    Disagreement does not discard the frame - the menu is still there - it just
    stops it counting as a choice the scenario acts on.
    """

    OPTIONS = ["「はい」", "「いいえ」"]

    def frame_with_table(self, count: int, targets: tuple[int, ...]) -> bytes:
        frame = menu(self.OPTIONS)
        frame += overlay.CHOICE_BRANCH + bytes((count, 0))
        frame += b"".join(struct.pack("<I", target) for target in targets)
        return frame

    def test_a_table_shorter_than_the_options_does_not_confirm(self) -> None:
        block = overlay.read_choice_frame(self.frame_with_table(1, (4,)), 0)
        assert block is not None
        self.assertEqual(len(block.options), 2)
        self.assertFalse(block.branches)

    def test_a_table_running_past_the_end_does_not_confirm(self) -> None:
        block = overlay.read_choice_frame(self.frame_with_table(2, (4,)), 0)
        assert block is not None
        self.assertFalse(block.branches)

    def test_a_target_outside_the_script_does_not_confirm(self) -> None:
        """A plausible table has to point somewhere the script actually is."""
        block = overlay.read_choice_frame(menu(self.OPTIONS, (4, 1 << 30)), 0)
        assert block is not None
        self.assertFalse(block.branches)

    def test_a_zero_target_does_not_confirm(self) -> None:
        block = overlay.read_choice_frame(menu(self.OPTIONS, (0, 4)), 0)
        assert block is not None
        self.assertFalse(block.branches)


class TestScanning(unittest.TestCase):
    def test_blocks_come_back_in_file_order(self) -> None:
        data = (
            menu(["「あ」", "「い」"], (4, 8))
            + message("これは本文です。")
            + menu(["「う」", "「え」", "「お」"])
        )
        blocks = overlay.find_choice_blocks(data)
        self.assertEqual([len(block.options) for block in blocks], [2, 3])
        self.assertEqual([block.branches for block in blocks], [True, False])
        self.assertLess(blocks[0].offset, blocks[1].offset)

    def test_a_broken_frame_does_not_hide_a_later_one(self) -> None:
        """Scanning continues one byte past a rejected marker, so a frame that
        fails its own checks cannot swallow the frame that follows it."""
        broken = menu(["「あ」", "「い」"]).replace(overlay.CHOICE_CLOSE, b"")
        blocks = overlay.find_choice_blocks(broken + menu(["「う」", "「え」"]))
        self.assertEqual(len(blocks), 1)
        self.assertEqual(
            [text.decode("cp932") for _, text in blocks[0].options], ["「う」", "「え」"]
        )

    def test_a_script_without_menus_yields_nothing(self) -> None:
        self.assertEqual(overlay.find_choice_blocks(message("本文だけ。")), [])


class TestWorkbookLabels(unittest.TestCase):
    def test_a_branching_menu_becomes_a_c_window(self) -> None:
        windows = labels(menu(["「はい」", "「いいえ」"], (4, 8)))
        self.assertEqual(sorted(windows), ["C00001"])
        self.assertEqual(windows["C00001"], ["「はい」", "「いいえ」"])

    def test_a_menu_the_scenario_ignores_stays_a_d_window(self) -> None:
        """The trailing block of the shipped script is a developer's state
        picker: the same opcode, no branch on the answer, and lines phrased as
        facts. Labelling it a choice would invite rewriting facts as actions."""
        windows = labels(menu(["「見た」", "「見ていない」"]))
        self.assertEqual(sorted(windows), ["D00001"])

    def test_menus_are_numbered_from_one_in_file_order(self) -> None:
        data = (
            menu(["「あ」", "「い」"], (4, 8))
            + menu(["「う」", "「え」"], (4, 8))
        )
        windows = labels(data)
        self.assertEqual(sorted(windows), ["C00001", "C00002"])
        self.assertEqual(windows["C00001"], ["「あ」", "「い」"])
        self.assertEqual(windows["C00002"], ["「う」", "「え」"])

    def test_a_menu_still_consumes_a_dialog_number(self) -> None:
        """Otherwise adding the C series would renumber every window after the
        first menu, and a proofread rule names the window it fixes."""
        data = (
            message("一つ目の本文。") + BOUNDARY
            + menu(["「あ」", "「い」"], (4, 8))
            + message("二つ目の本文。") + BOUNDARY
        )
        windows = labels(data)
        self.assertEqual(sorted(windows), ["C00001", "D00001", "D00003"])

    def test_line_positions_are_numbered_within_the_menu(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "RSAN.SD"
            path.write_bytes(menu(["「あ」", "「い」", "「う」"], (4, 8, 12)))
            entries = overlay.extract_entries(path)
        self.assertEqual(
            [entry.dialog_lines for entry in entries],
            [("C00001 1/3",), ("C00001 2/3",), ("C00001 3/3",)],
        )

    def test_a_menu_sharing_a_window_with_a_message_makes_the_whole_window_c(
        self,
    ) -> None:
        """Nothing separates a message from a menu that follows it immediately,
        so the two can land in one window. The window is choice text then: its
        options may not be joined into the message's sentence, and a cell that
        said both C and D would name two ways of translating one line."""
        windows = labels(message("直前の本文。") + menu(["「あ」", "「い」"], (4, 8)))
        self.assertEqual(sorted(windows), ["C00001"])
        self.assertEqual(len(windows["C00001"]), 3)

    def test_a_message_repeating_an_option_is_choice_text_too(self) -> None:
        """The script echoes the picked option as narration a moment later. It
        is the same string, and the overlay replaces strings, so both places
        show the same words - which have to work as an option, so the echo is
        labelled and translated the same way."""
        windows = labels(
            menu(["「あ」", "「い」"], (4, 8)) + message("「あ」") + BOUNDARY
        )
        self.assertEqual(sorted(windows), ["C00001", "C00002"])
        self.assertEqual(windows["C00002"], ["「あ」"])

    def test_the_label_spreads_along_shared_lines_until_it_stops(self) -> None:
        """A window pulled in by one shared line makes its own lines standalone
        in turn, so the next window sharing one of those follows. The chain has
        to be walked to the end or a cell at the far end still mixes labels."""
        windows = labels(
            menu(["「あ」", "「い」"], (4, 8))
            + message("「あ」") + message("二行目。") + BOUNDARY
            + message("二行目。") + message("三行目。") + BOUNDARY
            + message("よそ様の本文。") + BOUNDARY
        )
        self.assertEqual(sorted(windows), ["C00001", "C00002", "C00003", "D00004"])
        self.assertEqual(windows["D00004"], ["よそ様の本文。"])

    def test_no_cell_ever_mixes_the_two_series(self) -> None:
        """The one rule the labelling exists to keep."""
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "RSAN.SD"
            path.write_bytes(
                menu(["「あ」", "「い」"], (4, 8))
                + message("「あ」") + message("二行目。") + BOUNDARY
                + message("二行目。") + BOUNDARY
            )
            entries = overlay.extract_entries(path)
        for entry in entries:
            series = {location[0] for location in entry.dialog_lines}
            self.assertEqual(len(series), 1, entry.dialog_lines)


@requires_pinned_scenario
class TestShippedScript(unittest.TestCase):
    """Numbers taken from the supported RSAN.SD, not invented."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.path = scenario_path()
        cls.data = cls.path.read_bytes()
        cls.blocks = overlay.find_choice_blocks(cls.data)
        cls.entries = overlay.extract_entries(cls.path)

    def test_the_script_holds_twenty_eight_menus(self) -> None:
        self.assertEqual(len(self.blocks), 28)

    def test_sixteen_of_them_branch(self) -> None:
        branching = [block for block in self.blocks if block.branches]
        self.assertEqual(len(branching), 16)
        self.assertEqual(len(self.blocks) - len(branching), 12)

    def test_the_menus_that_do_not_branch_all_sit_after_the_story(self) -> None:
        """They are packed into the trailing code block, past the last line of
        narration - which is the second reason not to read them as choices."""
        last_choice = max(b.offset for b in self.blocks if b.branches)
        first_picker = min(b.offset for b in self.blocks if not b.branches)
        self.assertLess(last_choice, first_picker)
        self.assertGreater(first_picker, 0xD1A00)

    def test_no_row_carries_both_series(self) -> None:
        """The invariant the whole labelling exists for: a line is either choice
        text or message text, and one line cannot be translated both ways."""
        for entry in self.entries:
            series = {location[0] for location in entry.dialog_lines}
            with self.subTest(text=entry.text):
                self.assertEqual(len(series), 1)

    def test_the_c_series_covers_the_menus_and_their_two_echoes(self) -> None:
        """Sixteen menus, plus the two windows that repeat an option as
        narration a moment after it is picked."""
        found = sum(1 for block in self.blocks if block.branches)
        labelled = {
            location.split()[0]
            for entry in self.entries
            for location in entry.dialog_lines
            if location.startswith("C")
        }
        self.assertEqual(found, 16)
        self.assertEqual(len(labelled), 18)

    def test_the_choice_series_is_dense(self) -> None:
        numbers = sorted(
            int(location.split()[0][1:])
            for entry in self.entries
            for location in entry.dialog_lines
            if location.startswith("C")
        )
        self.assertEqual(sorted(set(numbers)), list(range(1, 19)))

    def test_the_echoes_hold_nothing_a_menu_does_not(self) -> None:
        """Which is why labelling them C costs the reviewer nothing: there is no
        second wording to write, the engine shows the one string in both
        places."""
        windows: dict[str, list[str]] = {}
        for entry in self.entries:
            for location in entry.dialog_lines:
                windows.setdefault(location.split()[0], []).append(entry.text)
        echoes = {"C00003": ["麗佳の苦痛に彩られた声…"], "C00012": ["危なかったな…"]}
        for name, lines in echoes.items():
            with self.subTest(window=name):
                self.assertEqual(windows[name], lines)
                self.assertEqual(
                    sum(1 for text in windows["C00002"] if text in lines)
                    + sum(1 for text in windows["C00011"] if text in lines),
                    1,
                )

    def test_choices_take_twenty_nine_rows(self) -> None:
        """Fewer than the thirty-two option slots: three lines are worded the
        same in two menus, and rows are deduplicated by source bytes."""
        rows = [
            entry
            for entry in self.entries
            if any(location.startswith("C") for location in entry.dialog_lines)
        ]
        self.assertEqual(len(rows), 29)

    def test_the_d_series_numbers_every_window_it_reaches(self) -> None:
        """The C series spends its numbers out of the same run as the D series.

        Eighteen numbers go to choice windows and are left unused in the D
        series, so the last window is numbered for the total rather than for the
        count of D windows.

        The total moved from 8564 to 9987 when the voiced lines came back, then
        to 10014 when punctuation-only voiced quotes were admitted. With it
        every D number after the first recovered window moves. That is why a
        proofread rule addresses a window by its source lines: a rule keyed on
        the label alone would now be pointing at a different scene.
        """
        names = {
            location.split()[0]
            for entry in self.entries
            for location in entry.dialog_lines
        }
        dialogs = sorted(int(name[1:]) for name in names if name.startswith("D"))
        self.assertEqual(len(names), 10014)
        self.assertEqual(len(dialogs), 9996)
        self.assertEqual(max(dialogs), 10014)

    def test_the_first_menu_reads_as_a_pair_of_actions(self) -> None:
        first = next(block for block in self.blocks if block.branches)
        self.assertEqual(
            [text.decode("cp932") for _, text in first.options],
            ["明日は性感帯のチェックをする", "明日はバイブを使った調教にする"],
        )

    def test_the_first_state_picker_reads_as_a_pair_of_facts(self) -> None:
        first = next(block for block in self.blocks if not block.branches)
        self.assertEqual(
            [text.decode("cp932") for _, text in first.options],
            ["ジョンに舐められてた", "ジョンに舐められてない"],
        )

    def test_every_option_survives_the_scenario_text_filter(self) -> None:
        """A menu option the extractor drops would leave a window short a line,
        and the workbook with no way to translate that option at all."""
        for block in self.blocks:
            for _, option in block.options:
                text = option.decode("cp932")
                with self.subTest(text=text):
                    self.assertTrue(overlay.looks_like_scenario_text(text))

    def test_every_option_reaches_a_workbook_row(self) -> None:
        rows = {entry.source for entry in self.entries}
        for block in self.blocks:
            for _, option in block.options:
                with self.subTest(text=option.decode("cp932")):
                    self.assertIn(option, rows)
