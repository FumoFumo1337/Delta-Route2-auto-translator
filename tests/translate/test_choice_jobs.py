"""Sending a choice menu to DeepL one option at a time.

An option is not half of a sentence. Joining a window of them produces a single
phrase sliced across the options, which is the defect the C series exists to
prevent, so a menu is sent one text per option and each answer is taken back
whole.

The extractor guarantees a cell never mixes the two series, but a workbook can
be older than the extractor that wrote it, so the same rule is closed again
here: a window holding a line that must stand alone is never joined. The
labelling those keys come from is tested in extract/test_choices.py.
"""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from context import TOOLS_ROOT  # noqa: F401  (prepares sys.path)

import delta_deepl


def workbook_row(source: str, dialog_line: str) -> delta_deepl.WorkbookRow:
    return delta_deepl.WorkbookRow(
        source=source,
        worksheet=None,
        row_index=0,
        target_column=0,
        dialog_line=dialog_line,
    )


def job(key: str, sources: list[str]) -> delta_deepl.DialogJob:
    rows = [
        workbook_row(source, f"{key} {index}/{len(sources)}")
        for index, source in enumerate(sources, start=1)
    ]
    return delta_deepl.DialogJob(
        key, rows, {row.source for row in rows}, key.startswith("C")
    )


class TestTranslationUnits(unittest.TestCase):
    """A menu costs one API text per option, a message window one per window."""

    def test_a_message_window_is_one_text(self) -> None:
        item = job("D00001", ["あいうえお", "かきくけこ"])
        self.assertFalse(item.per_line)
        self.assertEqual(item.text_count, 1)
        self.assertEqual(item.character_count, 10)

    def test_a_menu_is_one_text_per_option(self) -> None:
        item = job("C00001", ["あいうえお", "かきくけこ"])
        self.assertTrue(item.per_line)
        self.assertEqual(item.text_count, 2)
        self.assertEqual(item.character_count, 10)

    def test_the_item_limit_counts_texts_not_windows(self) -> None:
        """Otherwise a batch of 50 windows holding menus would send more texts
        than the limit the request was sized for."""
        menus = [job(f"C{index:05d}", ["あ", "い", "う"]) for index in range(1, 5)]
        batches = delta_deepl.iter_batches(menus, max_chars=10_000, max_items=6)
        self.assertEqual([len(batch) for batch in batches], [2, 2])

    def test_windows_batch_exactly_as_before(self) -> None:
        windows = [job(f"D{index:05d}", ["あ", "い"]) for index in range(1, 6)]
        batches = delta_deepl.iter_batches(windows, max_chars=10_000, max_items=2)
        self.assertEqual([len(batch) for batch in batches], [2, 2, 1])

    def test_a_character_budget_still_ends_a_batch(self) -> None:
        items = [job(f"C{index:05d}", ["あいうえお", "かきくけこ"]) for index in (1, 2)]
        batches = delta_deepl.iter_batches(items, max_chars=15, max_items=50)
        self.assertEqual([len(batch) for batch in batches], [1, 1])


class TestSendingChoices(unittest.TestCase):
    """What actually reaches the API, and how the answer is put back."""

    def setUp(self) -> None:
        self.deepl = delta_deepl
        self.sent: list[list[str]] = []
        self.original = delta_deepl.request_deepl

        def fake_request(
            api_key, api_url, body, expected_count, retries, cancel_file=None
        ):
            self.sent.append(list(body["text"]))
            if len(body["text"]) != expected_count:
                raise AssertionError(
                    f"expected_count {expected_count} does not match "
                    f"{len(body['text'])} texts"
                )
            return [f"TR{index}" for index in range(len(body["text"]))]

        delta_deepl.request_deepl = fake_request

    def tearDown(self) -> None:
        self.deepl.request_deepl = self.original

    def translate(self, dialogs, per_line, loop_retries=0):
        return self.deepl.call_deepl_dialogs(
            api_key="k",
            api_url="u",
            source_lang="JA",
            target_lang="RU",
            dialogs=dialogs,
            retries=1,
            per_line=per_line,
            loop_retries=loop_retries,
        )

    def test_a_message_window_goes_as_one_joined_text(self) -> None:
        result = self.translate([["あいう", "えおか"]], [False])
        self.assertEqual(self.sent, [["あいうえおか"]])
        self.assertEqual(len(result[0]), 2)

    def test_a_menu_goes_as_one_text_per_option(self) -> None:
        result = self.translate([["あいう", "えおか"]], [True])
        self.assertEqual(self.sent, [["あいう", "えおか"]])
        self.assertEqual(result, [["TR0", "TR1"]])

    def test_each_option_comes_back_whole(self) -> None:
        """The defect this replaces: two options were one sentence cut in half,
        so the first ended mid-clause and the second began in lower case."""
        result = self.translate([["あいうえおかきくけこ", "さしすせそ"]], [True])
        self.assertEqual(result, [["TR0", "TR1"]])
        for option in result[0]:
            self.assertTrue(option)

    def test_menus_and_windows_travel_in_one_request(self) -> None:
        result = self.translate(
            [["あ", "い"], ["う", "え"], ["お"]], [False, True, False]
        )
        self.assertEqual(self.sent, [["あい", "う", "え", "お"]])
        self.assertEqual([len(lines) for lines in result], [2, 2, 1])
        self.assertEqual(result[1], ["TR1", "TR2"])
        self.assertEqual(result[2], ["TR3"])

    def test_omitting_the_flag_keeps_the_old_behaviour(self) -> None:
        result = self.deepl.call_deepl_dialogs(
            api_key="k",
            api_url="u",
            source_lang="JA",
            target_lang="RU",
            dialogs=[["あいう", "えおか"]],
            retries=1,
        )
        self.assertEqual(self.sent, [["あいうえおか"]])
        self.assertEqual(len(result[0]), 2)

    def test_a_looped_message_is_retried_one_line_at_a_time(self) -> None:
        answers = iter(
            [
                ["Stop it, " * 80],
                ["Stop it, please.", "I cannot take any more."],
            ]
        )

        def fake_request(
            api_key, api_url, body, expected_count, retries, cancel_file=None
        ):
            self.sent.append(list(body["text"]))
            result = next(answers)
            self.assertEqual(len(result), expected_count)
            return result

        self.deepl.request_deepl = fake_request
        result = self.translate(
            [["止めてっ！お願いだからこんなのっ、", "止めてぇえぇえぇえぇえぇえぇえぇぇぇッ！！"]],
            [False],
            loop_retries=3,
        )
        self.assertEqual(
            self.sent,
            [
                ["止めてっ！お願いだからこんなのっ、止めてぇえぇえぇえぇえぇえぇえぇぇぇッ！！"],
                ["止めてっ！お願いだからこんなのっ、", "止めてぇえぇえぇえぇえぇえぇえぇぇぇッ！！"],
            ],
        )
        self.assertEqual(result, [["Stop it, please.", "I cannot take any more."]])

    def test_intentional_short_repetition_is_not_retried(self) -> None:
        repeated = "Ah... " * 10

        def fake_request(
            api_key, api_url, body, expected_count, retries, cancel_file=None
        ):
            self.sent.append(list(body["text"]))
            return [repeated]

        self.deepl.request_deepl = fake_request
        result = self.translate(
            [["あ、あ、あ、あ、あ、あ、あ、あ、", "あ、あ、あ、あ、あ、あ、あ、あ、"]],
            [False],
        )
        self.assertEqual(len(self.sent), 1)
        self.assertEqual(len(result[0]), 2)

    def test_a_menu_is_never_loop_retried(self) -> None:
        loop = "Again " * 80

        def fake_request(
            api_key, api_url, body, expected_count, retries, cancel_file=None
        ):
            self.sent.append(list(body["text"]))
            return [loop, loop]

        self.deepl.request_deepl = fake_request
        result = self.translate([["選択肢一", "選択肢二"]], [True])
        self.assertEqual(len(self.sent), 1)
        self.assertEqual(result, [[loop.strip(), loop.strip()]])

    def test_a_looped_per_line_answer_is_retried_at_most_three_times(self) -> None:
        loop = "Stop it, " * 80

        def fake_request(
            api_key, api_url, body, expected_count, retries, cancel_file=None
        ):
            self.sent.append(list(body["text"]))
            return [loop] * expected_count

        self.deepl.request_deepl = fake_request
        result = self.translate(
            [["止めて", "お願い"]], [False], loop_retries=3
        )
        # One original joined request, then the three guarded per-line retries.
        self.assertEqual(len(self.sent), 4)
        self.assertEqual([len(request) for request in self.sent], [1, 2, 2, 2])
        self.assertEqual(result, [[loop.strip(), loop.strip()]])

    def test_a_looped_single_line_is_also_retried_at_most_three_times(self) -> None:
        loop = "A" * 200

        def fake_request(
            api_key, api_url, body, expected_count, retries, cancel_file=None
        ):
            self.sent.append(list(body["text"]))
            return [loop]

        self.deepl.request_deepl = fake_request
        result = self.translate([["あぁッ！"]], [False], loop_retries=3)
        self.assertEqual(len(self.sent), 4)
        self.assertTrue(all(len(request) == 1 for request in self.sent))
        self.assertEqual(result, [[loop]])

    def test_content_loop_retries_are_disabled_by_default(self) -> None:
        loop = "Stop it, " * 80

        def fake_request(
            api_key, api_url, body, expected_count, retries, cancel_file=None
        ):
            self.sent.append(list(body["text"]))
            return [loop]

        self.deepl.request_deepl = fake_request
        result = self.translate([["止めて", "お願い"]], [False])
        self.assertEqual(len(self.sent), 1)
        self.assertEqual(len(result[0]), 2)


class TestLoopDetection(unittest.TestCase):
    def test_repeated_phrase_with_large_inflation_is_a_loop(self) -> None:
        self.assertTrue(
            delta_deepl.dialog_translation_looks_looped(
                "Stop it, " * 40, ["止めて", "お願い"], "JA"
            )
        )

    def test_long_character_run_with_large_inflation_is_a_loop(self) -> None:
        self.assertTrue(
            delta_deepl.dialog_translation_looks_looped(
                "Gwa" + "a" * 200, ["ぐぁッ！", "あぁッ！"], "JA"
            )
        )

    def test_repetition_matching_the_source_is_not_a_loop(self) -> None:
        self.assertFalse(
            delta_deepl.dialog_translation_looks_looped(
                "Ah... " * 10,
                ["あ、あ、あ、あ、あ、", "あ、あ、あ、あ、あ、"],
                "JA",
            )
        )


class TestUnresolvedLoopReport(unittest.TestCase):
    def test_report_name_uses_lowercase_language_suffix(self) -> None:
        self.assertEqual(
            delta_deepl.unresolved_loop_report_name("RU"),
            "deepl_unresolved_loops.ru.json",
        )
        self.assertEqual(
            delta_deepl.unresolved_loop_report_name("en"),
            "deepl_unresolved_loops.en.json",
        )

    def test_records_keep_context_for_manual_proofreading(self) -> None:
        item = job("D00001", ["止めて", "お願い"])
        records = delta_deepl.unresolved_loop_records(
            [item], [["Stop it, " * 40, ""]], "JA"
        )
        self.assertEqual(len(records), 1)
        self.assertEqual(records[0]["dialog"], "D00001")
        self.assertEqual(records[0]["source_lines"], ["止めて", "お願い"])
        self.assertEqual(records[0]["reason"], "word-sequence")

    def test_report_is_language_specific_and_resolved_windows_leave(self) -> None:
        folder = Path(self.enterContext(tempfile.TemporaryDirectory()))
        report = folder / delta_deepl.unresolved_loop_report_name("RU")
        ru = {
            "dialog": "D00002",
            "reason": "character-run",
            "source_lines": ["あ"],
            "translation_lines": ["A" * 200],
            "source_characters": 1,
            "translation_characters": 200,
        }
        en = dict(ru, dialog="D00003")
        delta_deepl.update_unresolved_loop_report(
            report, "RU", {"D00002"}, [ru], folder / "source.xlsx", folder / "ru.xlsx"
        )
        en_report = folder / delta_deepl.unresolved_loop_report_name("EN")
        delta_deepl.update_unresolved_loop_report(
            en_report,
            "EN",
            {"D00003"},
            [en],
            folder / "source.xlsx",
            folder / "en.xlsx",
        )
        remaining = delta_deepl.update_unresolved_loop_report(
            report, "RU", {"D00002"}, [], folder / "source.xlsx", folder / "ru.xlsx"
        )
        stored = json.loads(report.read_text(encoding="utf-8"))
        stored_en = json.loads(en_report.read_text(encoding="utf-8"))
        self.assertEqual(remaining, 0)
        self.assertEqual(stored["language"], "RU")
        self.assertEqual(stored["dialogs"], [])
        self.assertEqual(stored_en["language"], "EN")
        self.assertEqual(stored_en["unresolved_count"], 1)


class TestChoiceCache(unittest.TestCase):
    """Menu options are cached apart, which is what makes the fix affordable.

    Bumping the one method marker would have invalidated all 27,736 entries and
    charged a full re-translation for 29 rows.
    """

    def entries(self, rows: list[dict[str, str]]) -> Path:
        folder = self.enterContext(tempfile.TemporaryDirectory())
        path = Path(folder) / "deepl_cache.jsonl"
        path.write_text(
            "".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows),
            encoding="utf-8",
        )
        return path

    def test_both_methods_survive_loading(self) -> None:
        path = self.entries(
            [
                {
                    "source": "あ",
                    "translation": "old",
                    "target_lang": "RU",
                    "method": delta_deepl.CACHE_METHOD,
                },
                {
                    "source": "あ",
                    "translation": "new",
                    "target_lang": "RU",
                    "method": delta_deepl.CHOICE_CACHE_METHOD,
                },
            ]
        )
        stored = delta_deepl.load_cache(path, "RU")
        self.assertEqual(
            stored["あ"],
            {
                delta_deepl.CACHE_METHOD: "old",
                delta_deepl.CHOICE_CACHE_METHOD: "new",
            },
        )

    def test_an_unknown_method_is_ignored(self) -> None:
        path = self.entries(
            [
                {
                    "source": "あ",
                    "translation": "x",
                    "target_lang": "RU",
                    "method": "some-older-method-v0",
                }
            ]
        )
        self.assertEqual(delta_deepl.load_cache(path, "RU"), {})

    def test_a_row_reads_the_series_it_belongs_to(self) -> None:
        stored = {
            "あ": {
                delta_deepl.CACHE_METHOD: "as a message",
                delta_deepl.CHOICE_CACHE_METHOD: "as an option",
            }
        }
        self.assertEqual(
            delta_deepl.cache_for_rows(stored, [workbook_row("あ", "D00001 1/1")]),
            {"あ": "as a message"},
        )
        self.assertEqual(
            delta_deepl.cache_for_rows(stored, [workbook_row("あ", "C00001 1/2")]),
            {"あ": "as an option"},
        )

    def test_a_menu_row_with_only_an_old_entry_is_uncached(self) -> None:
        """This is what re-translates the 29 option rows and nothing else."""
        stored = {"あ": {delta_deepl.CACHE_METHOD: "sliced"}}
        self.assertEqual(
            delta_deepl.cache_for_rows(stored, [workbook_row("あ", "C00001 1/2")]), {}
        )
        self.assertEqual(
            delta_deepl.cache_for_rows(stored, [workbook_row("あ", "D00001 1/1")]),
            {"あ": "sliced"},
        )

    def test_the_method_follows_the_line_not_the_label(self) -> None:
        self.assertEqual(
            delta_deepl.row_cache_method("あ", {"あ"}),
            delta_deepl.CHOICE_CACHE_METHOD,
        )
        self.assertEqual(
            delta_deepl.row_cache_method("あ", set()), delta_deepl.CACHE_METHOD
        )

    def test_a_workbook_that_still_mixes_the_series_is_cached_as_options(
        self,
    ) -> None:
        """Extract stopped emitting such a cell, but a workbook can be older
        than the extractor, and a message translated as an option still reads;
        a slice of somebody's sentence offered as an option does not."""
        stored = {
            "あ": {
                delta_deepl.CACHE_METHOD: "as a message",
                delta_deepl.CHOICE_CACHE_METHOD: "as an option",
            }
        }
        for cell in ("C00002 1/2; D00579 1/1", "D00579 1/1; C00002 1/2"):
            with self.subTest(cell=cell):
                self.assertEqual(
                    delta_deepl.cache_for_rows(stored, [workbook_row("あ", cell)]),
                    {"あ": "as an option"},
                )
        self.assertEqual(
            delta_deepl.cache_for_rows(
                stored, [workbook_row("あ", "D00100 1/2; D00200 1/2")]
            ),
            {"あ": "as a message"},
        )

    def test_written_entries_carry_the_method_they_were_made_with(self) -> None:
        path = self.entries([])
        delta_deepl.append_cache(
            path, "あ", "вариант", "RU", delta_deepl.CHOICE_CACHE_METHOD
        )
        written = json.loads(path.read_text(encoding="utf-8").strip())
        self.assertEqual(written["method"], delta_deepl.CHOICE_CACHE_METHOD)

    def test_both_markers_are_versioned(self) -> None:
        for marker in (delta_deepl.CACHE_METHOD, delta_deepl.CHOICE_CACHE_METHOD):
            with self.subTest(marker=marker):
                self.assertRegex(marker, r"-v\d+$")

    def test_the_two_markers_differ(self) -> None:
        self.assertNotEqual(delta_deepl.CACHE_METHOD, delta_deepl.CHOICE_CACHE_METHOD)


class TestDialogWindowCache(unittest.TestCase):
    """Scenario cache identity is a guarded D/C window, never bare source text."""

    def entries(self, rows: list[dict]) -> Path:
        folder = self.enterContext(tempfile.TemporaryDirectory())
        path = Path(folder) / "deepl_cache.jsonl"
        path.write_text(
            "".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows),
            encoding="utf-8",
        )
        return path

    def contextual_entry(
        self, dialog: str, translation: str, source: str = "同じ"
    ) -> dict:
        return {
            "dialog": dialog,
            "source_lines": [source],
            "translation_lines": [translation],
            "target_lang": "RU",
            "method": delta_deepl.DIALOG_CACHE_METHOD,
        }

    def test_the_same_source_keeps_different_dialog_translations(self) -> None:
        path = self.entries(
            [
                self.contextual_entry("D00001", "первый контекст"),
                self.contextual_entry("D00002", "второй контекст"),
            ]
        )
        jobs = [job("D00001", ["同じ"]), job("D00002", ["同じ"])]
        resolved, migrated = delta_deepl.cache_for_dialog_jobs(
            delta_deepl.load_dialog_cache(path, "RU"), {}, jobs
        )
        self.assertEqual(
            resolved,
            {
                "D00001": ("первый контекст",),
                "D00002": ("второй контекст",),
            },
        )
        self.assertEqual(migrated, set())

    def test_a_renumbered_dialog_must_still_match_its_source_signature(self) -> None:
        path = self.entries([self.contextual_entry("D00001", "не тот текст")])
        resolved, migrated = delta_deepl.cache_for_dialog_jobs(
            delta_deepl.load_dialog_cache(path, "RU"),
            {},
            [job("D00001", ["другой исходник"])],
        )
        self.assertEqual(resolved, {})
        self.assertEqual(migrated, set())

    def test_legacy_source_entries_migrate_without_an_api_call(self) -> None:
        item = job("D00001", ["あ", "い"])
        legacy = {
            "あ": {delta_deepl.CACHE_METHOD: "первая"},
            "い": {delta_deepl.CACHE_METHOD: "вторая"},
        }
        resolved, migrated = delta_deepl.cache_for_dialog_jobs({}, legacy, [item])
        self.assertEqual(resolved, {"D00001": ("первая", "вторая")})
        self.assertEqual(migrated, {"D00001"})

    def test_a_propagated_per_line_message_migrates_mixed_legacy_series(self) -> None:
        rows = [
            workbook_row("общая", "C00001 1/1; D00001 1/2"),
            workbook_row("хвост", "D00001 2/2"),
        ]
        item = delta_deepl.DialogJob(
            "D00001", rows, {row.source for row in rows}, True
        )
        legacy = {
            "общая": {
                delta_deepl.CACHE_METHOD: "неправильный срез",
                delta_deepl.CHOICE_CACHE_METHOD: "целый вариант",
            },
            "хвост": {delta_deepl.CACHE_METHOD: "старый хвост"},
        }
        resolved, migrated = delta_deepl.cache_for_dialog_jobs({}, legacy, [item])
        self.assertEqual(
            resolved, {"D00001": ("целый вариант", "старый хвост")}
        )
        self.assertEqual(migrated, {"D00001"})

    def test_written_entry_contains_the_whole_window_and_round_trips(self) -> None:
        path = self.entries([])
        item = job("C00001", ["はい", "いいえ"])
        delta_deepl.append_dialog_cache(
            path, item, ["Да", "Нет"], "RU"
        )
        written = json.loads(path.read_text(encoding="utf-8").strip())
        self.assertEqual(written["dialog"], "C00001")
        self.assertEqual(written["source_lines"], ["はい", "いいえ"])
        self.assertEqual(written["translation_lines"], ["Да", "Нет"])
        self.assertEqual(written["method"], delta_deepl.CHOICE_DIALOG_CACHE_METHOD)
        resolved, migrated = delta_deepl.cache_for_dialog_jobs(
            delta_deepl.load_dialog_cache(path, "RU"), {}, [item]
        )
        self.assertEqual(resolved, {"C00001": ("Да", "Нет")})
        self.assertEqual(migrated, set())

    def test_window_cache_markers_are_versioned_and_distinct(self) -> None:
        markers = {
            delta_deepl.DIALOG_CACHE_METHOD,
            delta_deepl.CHOICE_DIALOG_CACHE_METHOD,
        }
        self.assertEqual(len(markers), 2)
        for marker in markers:
            self.assertRegex(marker, r"-v\d+$")


class TestJobGrouping(unittest.TestCase):
    def test_a_shared_row_is_grouped_with_the_menu(self) -> None:
        """It has to be translated once, and only the option wording works in
        both places. Grouping it with the message window would put a sentence
        fragment on the menu."""
        all_rows = [
            workbook_row("あ", "C00002 1/2; D00579 1/1"),
            workbook_row("い", "C00002 2/2"),
        ]
        jobs = delta_deepl.build_dialog_jobs(all_rows, all_rows, {})
        self.assertEqual([item.key for item in jobs], ["C00002"])
        self.assertTrue(jobs[0].per_line)

    def test_a_message_window_is_still_grouped_by_its_own_id(self) -> None:
        all_rows = [
            workbook_row("あ", "D00100 1/2"),
            workbook_row("い", "D00100 2/2"),
        ]
        jobs = delta_deepl.build_dialog_jobs(all_rows, all_rows, {})
        self.assertEqual([item.key for item in jobs], ["D00100"])
        self.assertFalse(jobs[0].per_line)

    def test_a_window_holding_an_option_is_never_joined(self) -> None:
        """The rule the labelling enforces, enforced again where the text is
        actually assembled: an option may only be joined with options, so a
        window holding one goes line by line. Half of a joined sentence in the
        menu would be exactly the defect the C series was added to fix."""
        all_rows = [
            workbook_row("あ", "C00002 1/2; D00300 1/2"),
            workbook_row("い", "C00002 2/2"),
            workbook_row("う", "D00300 2/2"),
        ]
        jobs = {
            item.key: item
            for item in delta_deepl.build_dialog_jobs(all_rows, all_rows, {})
        }
        self.assertEqual(sorted(jobs), ["C00002", "D00300"])
        self.assertTrue(all(item.per_line for item in jobs.values()))
        self.assertEqual(jobs["D00300"].text_count, 2)

    def test_a_window_two_shares_away_is_not_joined_either(self) -> None:
        all_rows = [
            workbook_row("あ", "C00002 1/1"),
            workbook_row("あ", "D00300 1/2"),
            workbook_row("い", "D00300 2/2"),
            workbook_row("い", "D00400 1/2"),
            workbook_row("う", "D00400 2/2"),
            workbook_row("え", "D00500 1/1"),
        ]
        jobs = {
            item.key: item.per_line
            for item in delta_deepl.build_dialog_jobs(all_rows, all_rows, {})
        }
        self.assertEqual(
            jobs, {"C00002": True, "D00300": True, "D00400": True, "D00500": False}
        )

    def test_a_menu_cannot_be_built_as_one_joined_text(self) -> None:
        with self.assertRaises(ValueError):
            delta_deepl.DialogJob("C00001", [], set(), False)
