"""How much of a workbook is translated, counted before an overlay is built.

verify_source hashes RSAN.SD, so the input to the pipeline is guarded. Nothing
guarded the output: a workbook left half-filled by an interrupted translation
run built an overlay just as happily as a finished one, and the gap showed up
only as Japanese still on screen.

Windows matter separately from rows. A window whose lines are partly translated
puts two languages in one message box, which is worse to read than either
language alone.
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from context import requires_openpyxl  # noqa: F401  (prepares sys.path)

import delta_overlay as overlay


HEADERS = ["ID", "Offset", "Speaker", "Dialog Line", "Original", "TL",
           "Occurrences", "Notes", "SourceBytesBase64"]


@requires_openpyxl
class CoverageCase(unittest.TestCase):
    def workbook(self, rows: list[tuple[str, str, str]], headers=None) -> Path:
        """A minimal Scenario sheet: (dialog lines, original, translation)."""
        import openpyxl

        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        path = directory / "book.xlsx"
        book = openpyxl.Workbook()
        sheet = book.active
        sheet.title = "Scenario"
        sheet.append(headers if headers is not None else HEADERS)
        for index, (windows, original, translation) in enumerate(rows, start=1):
            record = [index, "0x000000", "", windows, original, translation, 1, "", ""]
            if headers is not None:
                record = [record[HEADERS.index(name)] for name in headers]
            sheet.append(record)
        book.save(path)
        return path


class TestRowCounts(CoverageCase):
    def test_everything_translated(self) -> None:
        report = overlay.workbook_coverage(
            self.workbook([("D1 1/1", "日本語", "перевод")])
        )
        self.assertEqual((report.rows, report.translated, report.missing), (1, 1, 0))
        self.assertEqual(report.share, 1.0)

    def test_empty_translations_are_counted(self) -> None:
        report = overlay.workbook_coverage(
            self.workbook(
                [
                    ("D1 1/2", "日本語", "перевод"),
                    ("D1 2/2", "もっと", ""),
                    ("D2 1/1", "また", ""),
                ]
            )
        )
        self.assertEqual((report.rows, report.translated, report.missing), (3, 1, 2))

    def test_whitespace_is_not_a_translation(self) -> None:
        """An Excel cell holding a space looks filled and translates nothing."""
        report = overlay.workbook_coverage(self.workbook([("D1 1/1", "日本語", "   ")]))
        self.assertEqual(report.translated, 0)

    def test_rows_without_an_original_are_not_counted(self) -> None:
        """Trailing blank rows are ordinary in a hand-edited workbook and must
        not be reported as untranslated work."""
        report = overlay.workbook_coverage(
            self.workbook([("D1 1/1", "日本語", "перевод"), ("", "", "")])
        )
        self.assertEqual(report.rows, 1)

    def test_samples_name_untranslated_lines(self) -> None:
        report = overlay.workbook_coverage(
            self.workbook([("D1 1/1", "見本", ""), ("D2 1/1", "第二", "")])
        )
        self.assertIn("見本", report.untranslated_samples)

    def test_samples_are_capped(self) -> None:
        """A workbook nobody has started must not print thousands of lines."""
        rows = [(f"D{index} 1/1", f"行{index}", "") for index in range(50)]
        report = overlay.workbook_coverage(self.workbook(rows))
        self.assertEqual(report.rows, 50)
        self.assertLessEqual(len(report.untranslated_samples), 5)


class TestWindowCounts(CoverageCase):
    def test_a_window_is_complete_when_all_its_lines_are(self) -> None:
        report = overlay.workbook_coverage(
            self.workbook([("D1 1/2", "一", "раз"), ("D1 2/2", "二", "два")])
        )
        self.assertEqual(report.windows, 1)
        self.assertEqual(report.complete_windows, 1)
        self.assertEqual((report.partial_windows, report.empty_windows), (0, 0))

    def test_a_half_filled_window_is_partial(self) -> None:
        """This is the case the report exists for."""
        report = overlay.workbook_coverage(
            self.workbook([("D1 1/2", "一", "раз"), ("D1 2/2", "二", "")])
        )
        self.assertEqual(
            (report.complete_windows, report.partial_windows, report.empty_windows),
            (0, 1, 0),
        )

    def test_an_untouched_window_is_not_called_partial(self) -> None:
        report = overlay.workbook_coverage(
            self.workbook([("D1 1/2", "一", ""), ("D1 2/2", "二", "")])
        )
        self.assertEqual(
            (report.complete_windows, report.partial_windows, report.empty_windows),
            (0, 0, 1),
        )

    def test_a_row_shared_by_several_windows_counts_in_each(self) -> None:
        """Rows are deduplicated by source bytes, so one line recurs across the
        script. Counting it once would undercount the windows it appears in."""
        report = overlay.workbook_coverage(
            self.workbook(
                [
                    ("D1 1/2; D2 2/2", "はい", "да"),
                    ("D1 2/2", "二", ""),
                    ("D2 1/2", "三", "три"),
                ]
            )
        )
        self.assertEqual(report.windows, 2)
        self.assertEqual(report.complete_windows, 1)  # D2
        self.assertEqual(report.partial_windows, 1)  # D1

    def test_the_three_window_kinds_add_up(self) -> None:
        report = overlay.workbook_coverage(
            self.workbook(
                [
                    ("D1 1/1", "一", "раз"),
                    ("D2 1/2", "二", "два"),
                    ("D2 2/2", "三", ""),
                    ("D3 1/1", "四", ""),
                ]
            )
        )
        self.assertEqual(
            report.complete_windows + report.partial_windows + report.empty_windows,
            report.windows,
        )


class TestOlderWorkbooks(CoverageCase):
    def test_a_workbook_without_dialog_lines_still_reports_rows(self) -> None:
        """Workbooks predate the Dialog Line column, and proofread.py already
        treats it as optional. Losing the window figures is acceptable; refusing
        to build is not."""
        headers = [name for name in HEADERS if name != "Dialog Line"]
        report = overlay.workbook_coverage(
            self.workbook([("", "日本語", "перевод"), ("", "もっと", "")], headers)
        )
        self.assertEqual((report.rows, report.translated), (2, 1))
        self.assertEqual(report.windows, 0)

    def test_a_workbook_without_a_tl_column_is_refused(self) -> None:
        """Unlike Dialog Line, this one cannot be worked around: without it
        there is nothing to report and nothing to build."""
        headers = [name for name in HEADERS if name != "TL"]
        with self.assertRaises(ValueError):
            overlay.workbook_coverage(
                self.workbook([("D1 1/1", "日本語", "")], headers)
            )


if __name__ == "__main__":
    unittest.main()
