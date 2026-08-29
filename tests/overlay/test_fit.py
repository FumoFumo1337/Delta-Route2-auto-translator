"""Line measurement against the message window.

The model is small and the arithmetic is the proxy's, so most of what is worth
testing is that this module says the same thing DeltaOverlay.cpp does. The
constants are checked against that source directly, because the two drifting
apart is the failure that would make every measurement quietly wrong.
"""

from __future__ import annotations

import re
import unittest
from pathlib import Path

from context import TOOLS_ROOT  # noqa: F401  (prepares sys.path)

import delta_fit


PROXY_SOURCE = (TOOLS_ROOT / "runtime_proxy" / "DeltaOverlay.cpp").read_text(
    encoding="utf-8"
)


def requires_measurement(test: object) -> object:
    reason = delta_fit.unavailable_reason()
    if reason is not None:
        return unittest.skip(reason)(test)
    return test


class TestMetrics(unittest.TestCase):
    def test_defaults_match_the_proxy(self) -> None:
        """These are duplicated from C++ and nothing links the two copies.

        A layout default changed on one side only would move every measurement
        away from what the game draws, without any test noticing.
        """
        for name, value in (
            ("TextOriginX", delta_fit.DEFAULT_TEXT_X),
            ("MessageFontHeight", delta_fit.DEFAULT_FONT_HEIGHT),
            ("LetterSpacing", delta_fit.DEFAULT_LETTER_SPACING),
        ):
            with self.subTest(constant=name):
                match = re.search(rf"^int {name} = (-?\d+);", PROXY_SOURCE, re.M)
                self.assertIsNotNone(match, f"{name} is no longer declared this way")
                assert match is not None
                self.assertEqual(int(match.group(1)), value)

    def test_font_face_matches_the_proxy(self) -> None:
        self.assertIn(f'CustomFontName = L"{delta_fit.FONT_FACE}"', PROXY_SOURCE)

    def test_available_width_is_the_frame_minus_the_margin(self) -> None:
        metrics = delta_fit.Metrics(text_x=32)
        self.assertEqual(metrics.available_width, 4 + 792 - 32)

    def test_a_wider_margin_leaves_less_room(self) -> None:
        self.assertLess(
            delta_fit.Metrics(text_x=96).available_width,
            delta_fit.Metrics(text_x=32).available_width,
        )

    def test_name_plate_step_is_the_half_width_cell(self) -> None:
        """Standalone strings keep the original grid, which is (height + 2) / 2."""
        self.assertEqual(delta_fit.Metrics(font_height=20).name_plate_step, 11)
        self.assertEqual(delta_fit.Metrics(font_height=24).name_plate_step, 13)


class TestReadMetrics(unittest.TestCase):
    def setUp(self) -> None:
        self.ini = Path(self.enterContext(__import__("tempfile").TemporaryDirectory()))

    def write(self, body: str) -> Path:
        path = self.ini / "delta_launcher.ini"
        path.write_text(body, encoding="utf-8")
        return path

    def test_missing_file_gives_the_defaults(self) -> None:
        metrics = delta_fit.read_metrics(self.ini / "absent.ini")
        self.assertEqual(metrics, delta_fit.Metrics())

    def test_no_argument_gives_the_defaults(self) -> None:
        self.assertEqual(delta_fit.read_metrics(None), delta_fit.Metrics())

    def test_values_are_read(self) -> None:
        path = self.write("[Overlay]\nTEXT_X=16\nFONT_HEIGHT=14\nLETTER_SPACING=1\n")
        metrics = delta_fit.read_metrics(path)
        self.assertEqual(
            (metrics.text_x, metrics.font_height, metrics.letter_spacing), (16, 14, 1)
        )

    def test_a_partial_section_keeps_the_other_defaults(self) -> None:
        path = self.write("[Overlay]\nFONT_HEIGHT=18\n")
        metrics = delta_fit.read_metrics(path)
        self.assertEqual(metrics.font_height, 18)
        self.assertEqual(metrics.text_x, delta_fit.DEFAULT_TEXT_X)

    def test_an_unrelated_file_gives_the_defaults(self) -> None:
        """The launcher ini also holds a [Launcher] section and nothing else."""
        path = self.write("[Launcher]\nExecutable=RSA.EXE\nLanguage=RU\n")
        self.assertEqual(delta_fit.read_metrics(path), delta_fit.Metrics())


@requires_measurement
class TestMeasurement(unittest.TestCase):
    def setUp(self) -> None:
        self.fitter = delta_fit.Fitter()
        self.addCleanup(self.fitter.close)

    def test_empty_text_is_zero_wide(self) -> None:
        self.assertEqual(self.fitter.width(""), 0)

    def test_width_grows_with_the_text(self) -> None:
        short = self.fitter.width("Привет")
        long = self.fitter.width("Привет, как дела")
        self.assertGreater(long, short)

    def test_width_is_the_sum_of_the_glyph_steps(self) -> None:
        """The pen has no kerning: the proxy places each glyph on its own."""
        text = "Мама"
        self.assertEqual(
            self.fitter.width(text), sum(self.fitter.width(char) for char in text)
        )

    def test_letter_spacing_is_added_once_per_glyph(self) -> None:
        text = "тест"
        plain = delta_fit.Fitter(delta_fit.Metrics(letter_spacing=0))
        spaced = delta_fit.Fitter(delta_fit.Metrics(letter_spacing=2))
        self.addCleanup(plain.close)
        self.addCleanup(spaced.close)
        self.assertEqual(spaced.width(text) - plain.width(text), 2 * len(text))

    def test_a_narrow_glyph_is_narrower_than_a_wide_one(self) -> None:
        """A measurement that returned a constant would pass every test above."""
        self.assertLess(self.fitter.width("i"), self.fitter.width("Ж"))

    def test_minimum_step_raises_narrow_glyphs_only(self) -> None:
        """Name plates keep the half-width grid, so thin letters stop being thin."""
        step = self.fitter.metrics.name_plate_step
        self.assertEqual(self.fitter.width("iiii", step), 4 * step)
        self.assertGreaterEqual(self.fitter.width("ЖЖЖЖ", step), 4 * step)

    def test_multilingual_and_multiline_requests_are_stable(self) -> None:
        """The backend protocol must preserve UTF-8 and embedded newlines."""
        text = "Reika / Рейка\n次の行"
        self.assertEqual(
            self.fitter.width(text),
            sum(self.fitter.width(character) for character in text),
        )

    def test_overflow_is_measured_from_the_frame(self) -> None:
        width = self.fitter.metrics.available_width
        self.assertLessEqual(self.fitter.overflow("Привет"), 0)
        self.assertEqual(
            self.fitter.overflow("i" * 4000), self.fitter.width("i" * 4000) - width
        )

    def test_a_line_that_cannot_fit_reports_overflow(self) -> None:
        self.assertGreater(self.fitter.overflow("Ж" * 200), 0)

    def test_a_missing_font_is_refused_rather_than_substituted(self) -> None:
        """GDI silently substitutes, which would return measurements for the
        wrong face while looking perfectly healthy."""
        with self.assertRaises(LookupError):
            delta_fit.Fitter(delta_fit.Metrics(face="No Such Face At All"))


if __name__ == "__main__":
    unittest.main()
