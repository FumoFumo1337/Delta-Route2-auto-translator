"""Does a translated line still fit inside the message window?

The old proxy measured nothing: it counted half-width cells and warned past 90.
That number describes the Japanese grid, where every cell is `(fontHeight+2)/2`
wide. Translations are not drawn on that grid - the proxy places each glyph at a
measured pen position - so the count was calibrated against a layout the
translated text does not use, and 90 cells is about 990 px in a 792 px frame.

This module measures the line the way the proxy draws it, through the same
Win32 call on the same font, so the two agree by construction rather than by
approximation:

    step = max(abcA + abcB + abcC, abcA + abcB + max(-abcA, 0) + 1)
    pen += max(step, minimum_step) + letter_spacing

Measuring needs GDI and the font installed, so this is Windows only. Callers
are expected to check `available()` and report honestly when it is missing,
rather than fall back to a guess that looks like a measurement.
"""

from __future__ import annotations

import base64
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

# Message window geometry, from MS.MHU and the RVAs the proxy patches. The frame
# starts at x=4 and is 792 px wide; text begins at TEXT_X and must not reach the
# far edge.
FRAME_LEFT = 4
FRAME_WIDTH = 792

# The proxy's own defaults for a missing [Overlay] section, kept in step with
# DeltaOverlay.cpp.
DEFAULT_TEXT_X = 32
DEFAULT_FONT_HEIGHT = 20
DEFAULT_LETTER_SPACING = 0
FONT_FACE = "Arial Narrow"
TOOLS_ROOT = Path(__file__).resolve().parent.parent
RESOURCE_TOOL = TOOLS_ROOT / "bin" / "DeltaResourceTool.exe"


@dataclass(frozen=True)
class Metrics:
    """Everything that decides how wide a drawn line turns out."""

    text_x: int = DEFAULT_TEXT_X
    font_height: int = DEFAULT_FONT_HEIGHT
    letter_spacing: int = DEFAULT_LETTER_SPACING
    face: str = FONT_FACE

    @property
    def available_width(self) -> int:
        """Pixels between the text origin and the right edge of the frame."""
        return FRAME_LEFT + FRAME_WIDTH - self.text_x

    @property
    def name_plate_step(self) -> int:
        """Minimum advance the proxy uses for standalone strings.

        A name plate has its frame to itself, so it keeps the roomy half-width
        rhythm of the original instead of being squeezed.
        """
        return (self.font_height + 2) // 2


def read_metrics(ini_path: Path | None) -> Metrics:
    """Metrics from an [Overlay] section, falling back to the proxy defaults.

    The ini is the one beside RSA.EXE; a game that has never been launched with
    a custom layout simply has no file, which is not an error.
    """
    values: dict[str, int] = {}
    if ini_path is not None and ini_path.is_file():
        import configparser

        parser = configparser.ConfigParser()
        # The launcher writes this file, so it is ours; a malformed one is worth
        # a loud failure rather than a silent default.
        parser.read(ini_path, encoding="utf-8-sig")
        if parser.has_section("Overlay"):
            for key, name in (
                ("TEXT_X", "text_x"),
                ("FONT_HEIGHT", "font_height"),
                ("LETTER_SPACING", "letter_spacing"),
            ):
                if parser.has_option("Overlay", key):
                    values[name] = parser.getint("Overlay", key)
    return Metrics(**values)


class _Measurer:
    """Persistent client for the C# GDI measurement backend."""

    def __init__(self, metrics: Metrics) -> None:
        if not RESOURCE_TOOL.is_file():
            raise OSError(f"C# resource backend is missing: {RESOURCE_TOOL}")
        creation_flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        self._process = subprocess.Popen(
            [
                str(RESOURCE_TOOL), "fit", "server",
                "--font-height", str(metrics.font_height),
                "--letter-spacing", str(metrics.letter_spacing),
                "--face", metrics.face,
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            bufsize=1,
            creationflags=creation_flags,
        )
        assert self._process.stdout is not None
        if self._process.stdout.readline().strip() != "READY":
            assert self._process.stderr is not None
            message = self._process.stderr.read().strip()
            self._process.wait()
            self._close_streams()
            if "is not installed; GDI substituted" in message:
                raise LookupError(message.removeprefix("error: "))
            raise OSError(message or "C# text measurement backend did not start")

    def width(self, text: str, minimum_step: int) -> int:
        process = self._process
        if process.poll() is not None:
            raise OSError("C# text measurement backend stopped unexpectedly")
        assert process.stdin is not None and process.stdout is not None
        encoded = base64.b64encode(text.encode("utf-8")).decode("ascii")
        process.stdin.write(f"{minimum_step}\t{encoded}\n")
        process.stdin.flush()
        response = process.stdout.readline().strip()
        if not response:
            assert process.stderr is not None
            raise OSError(process.stderr.read().strip() or "No text measurement response")
        return int(response)

    def step(self, character: str) -> int:
        return self.width(character, 0)

    def close(self) -> None:
        process = getattr(self, "_process", None)
        if process is None:
            return
        self._process = None
        if process.poll() is None:
            try:
                assert process.stdin is not None
                process.stdin.write("QUIT\n")
                process.stdin.flush()
                process.stdin.close()
                process.wait(timeout=5)
            except (BrokenPipeError, OSError, subprocess.TimeoutExpired):
                process.kill()
                process.wait()
        self._close_streams(process)

    def _close_streams(self, process: subprocess.Popen[str] | None = None) -> None:
        process = process or getattr(self, "_process", None)
        if process is None:
            return
        for stream in (process.stdin, process.stdout, process.stderr):
            if stream is not None and not stream.closed:
                stream.close()

    def __enter__(self) -> "_Measurer":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


def available() -> bool:
    """Whether this machine can measure at all."""
    return sys.platform == "win32" and RESOURCE_TOOL.is_file()


def unavailable_reason(metrics: Metrics | None = None) -> str | None:
    """None when measuring works here, otherwise why it does not."""
    if not available():
        if sys.platform != "win32":
            return f"text measurement needs Windows, not {sys.platform}"
        return f"C# resource backend is missing: {RESOURCE_TOOL}"
    try:
        _Measurer(metrics or Metrics()).close()
    except (OSError, LookupError) as error:
        return str(error)
    return None


class Fitter:
    """Measures lines against one set of metrics."""

    def __init__(self, metrics: Metrics | None = None) -> None:
        self.metrics = metrics or Metrics()
        self._measurer = _Measurer(self.metrics)

    def width(self, text: str, minimum_step: int = 0) -> int:
        """Pixels the proxy advances the pen over this line."""
        return self._measurer.width(text, minimum_step)

    def overflow(self, text: str, minimum_step: int = 0) -> int:
        """Pixels past the right edge of the frame; zero or less when it fits."""
        return self.width(text, minimum_step) - self.metrics.available_width

    def close(self) -> None:
        self._measurer.close()

    def __enter__(self) -> "Fitter":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()
