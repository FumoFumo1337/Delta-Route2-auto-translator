"""Shared setup for the test suite.

The pipeline modules live in py/ and are imported by filename rather than as a
package, so the path is prepared here once instead of in every test file.

Some tests need the game itself. Those are skipped rather than failed when it is
absent, so the suite still runs on a checkout without a copy of the VN.
"""

from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS_ROOT = Path(__file__).resolve().parent.parent
PY_DIR = TOOLS_ROOT / "py"
RESOURCE_TOOL = TOOLS_ROOT / "bin" / "DeltaResourceTool.exe"

# A test module sits one folder below the data it reads, so the fixture and
# reference folders are named from the root rather than relative to __file__.
FIXTURES_DIR = TOOLS_ROOT / "tests" / "fixtures"
GOLDEN_DIR = TOOLS_ROOT / "tests" / "golden"

# Scratch files stay inside the checkout rather than going to the system temp
# folder, which is on the system drive and not on the one the project lives on.
# The suite writes workbooks and rebuilt artwork by the megabyte, so that is a
# lot of traffic somewhere the user did not put the project. tempfile.tempdir
# covers this process, TEMP and TMP carry the same folder into the tools the
# tests shell out to. TemporaryDirectory still removes what it makes, so the
# folder is left empty between runs.
SCRATCH_DIR = TOOLS_ROOT / "tests" / ".tmp"
SCRATCH_DIR.mkdir(parents=True, exist_ok=True)
tempfile.tempdir = str(SCRATCH_DIR)
os.environ["TEMP"] = str(SCRATCH_DIR)
os.environ["TMP"] = str(SCRATCH_DIR)

if str(PY_DIR) not in sys.path:
    sys.path.insert(0, str(PY_DIR))


def find_game_dir() -> Path | None:
    """The explicitly selected game folder, or None for portable tests.

    Unit tests must not change meaning merely because a checkout was copied
    beside a game.  Real-game integration checks are opt-in through
    DELTA_TEST_GAME; the normal suite uses checked-in fixtures.
    """
    override = os.environ.get("DELTA_TEST_GAME")
    if not override:
        return None
    candidate = Path(override)
    return candidate if (candidate / "RSAN.SD").is_file() else None


GAME_DIR = find_game_dir()


def requires_game(test: object) -> object:
    """Skips a test when the game is not on this machine."""
    return unittest.skipIf(GAME_DIR is None, "no game folder with RSAN.SD found")(test)


def scenario_path() -> Path:
    assert GAME_DIR is not None
    return GAME_DIR / "RSAN.SD"


def requires_pinned_scenario(test: object) -> object:
    """Skips golden tests unless RSAN.SD is the build their numbers describe.

    Row counts and sample lines are properties of one script. Against another
    title they would report failures that mean nothing, so the pin is checked
    against the hash the pipeline itself enforces in verify_source.
    """
    if GAME_DIR is None:
        return unittest.skip("no game folder with RSAN.SD found")(test)
    import delta_overlay

    try:
        digest = delta_overlay.sha256(scenario_path())
    except OSError as error:
        return unittest.skip(f"RSAN.SD cannot be read: {error}")(test)
    if digest != delta_overlay.SUPPORTED_SD_SHA256:
        return unittest.skip(f"RSAN.SD is a different build ({digest[:12]})")(test)
    return test


def requires_openpyxl(test: object) -> object:
    try:
        import openpyxl  # noqa: F401
    except ImportError:
        return unittest.skip("openpyxl is not installed")(test)
    return test


def requires_garbro(test: object) -> object:
    """Skips a test when the compiled GARbro-backed resource tool is absent."""
    if not RESOURCE_TOOL.is_file():
        return unittest.skip("DeltaResourceTool.exe has not been built")(test)
    for assembly in ("GameRes.dll", "ArcFormats.dll"):
        if not (RESOURCE_TOOL.parent / assembly).is_file():
            return unittest.skip(f"{assembly} is missing beside DeltaResourceTool.exe")(test)
    return test

