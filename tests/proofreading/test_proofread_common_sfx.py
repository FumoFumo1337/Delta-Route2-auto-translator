"""Regression tests for the shared Japanese SFX proofreading rules."""

from __future__ import annotations

import json
import re
import unittest

from context import FIXTURES_DIR, TOOLS_ROOT

import proofread


FIXTURE_PATH = FIXTURES_DIR / "proofread_common_sfx.json"


class TestProofreadCommonSfx(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))
        cls.profiles = {
            language: proofread.load_rules(
                TOOLS_ROOT / "profiles" / f"proofread_common.{language}.json"
            )
            for language in ("ru", "en")
        }

    def assert_fixture_cases(self, language: str) -> None:
        seen: set[str] = set()
        for case in self.fixture[language]:
            with self.subTest(language=language, case=case["id"]):
                self.assertNotIn(case["id"], seen)
                seen.add(case["id"])
                self.assertEqual(
                    proofread.apply_rule_block(
                        case["source"], case["input"], self.profiles[language]
                    ),
                    case["expected"],
                )

    def test_russian_sfx_fixture(self) -> None:
        self.assert_fixture_cases("ru")

    def test_english_sfx_fixture(self) -> None:
        self.assert_fixture_cases("en")

    def test_source_patterns_compile_and_do_not_use_dot_star(self) -> None:
        """SFX matching may enumerate punctuation, but must not absorb prose."""
        for language, profile in self.profiles.items():
            for index, rule in enumerate(profile.get("source_rules", [])):
                pattern = rule.get("source_pattern")
                if pattern is None:
                    continue
                with self.subTest(language=language, rule=index):
                    re.compile(pattern)
                    self.assertNotIn(".*", pattern)

    def test_fixture_has_both_positive_and_guard_cases(self) -> None:
        for language in ("ru", "en"):
            cases = self.fixture[language]
            self.assertGreaterEqual(len(cases), 2)
            self.assertTrue(any(case["input"] != case["expected"] for case in cases))
            self.assertTrue(any(case["input"] == case["expected"] for case in cases))


if __name__ == "__main__":
    unittest.main()
