"""Translate Delta/Route2 executable menu catalogs with DeepL.

The C# resource backend owns executable extraction and runtime TSV generation.
Only the network-facing translation step remains here.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Any

from delta_deepl import CACHE_METHOD, call_deepl, load_cache, append_cache


def read_catalog(path: Path) -> dict[str, Any]:
    # utf-8-sig, not utf-8: this catalog is filled in by hand when there is
    # no DeepL token, and Notepad writes a BOM that json.loads rejects.
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or not isinstance(value.get("entries"), list):
        raise ValueError("Menu catalog must contain an entries array")
    return value


def translate_catalog(
    catalog_path: Path,
    output_path: Path,
    language: str,
    cache_path: Path,
    api_url: str,
    retries: int,
    api_key: str = "",
    cancel_file: Path | None = None,
) -> None:
    language = language.upper()
    if language not in {"RU", "EN"}:
        raise ValueError("Menu target language must be RU or EN")
    catalog = read_catalog(catalog_path)
    entries = catalog["entries"]
    # A menu caption is one string on its own, so it shares the cache series of
    # a whole message window rather than the one for menu options - which is
    # what the entries written before the series existed already say.
    cache = {
        source: stored[CACHE_METHOD]
        for source, stored in load_cache(cache_path, language).items()
        if CACHE_METHOD in stored
    }
    pending = [
        item for item in entries
        if str(item.get(language.lower(), "")).strip() == ""
        and str(item.get("source", "")).strip()
    ]
    uncached = [str(item["source"]) for item in pending if str(item["source"]) not in cache]
    api_key = api_key or os.environ.get("DEEPL_API_KEY", "")
    if uncached and not api_key:
        raise ValueError("Set DEEPL_API_KEY or enter a token in the GUI")
    if uncached:
        print(f"Estimated API characters: {sum(len(source) for source in uncached)}")
        translations = call_deepl(
            api_key, api_url, "JA", language, uncached, retries, cancel_file
        )
        for source, translation in zip(uncached, translations):
            cache[source] = translation
            append_cache(cache_path, source, translation, language, CACHE_METHOD)
    for item in entries:
        source = str(item.get("source", ""))
        if not str(item.get(language.lower(), "")).strip() and source in cache:
            item[language.lower()] = cache[source]
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(
        json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"Menu entries: {len(entries)}")
    print(f"Translated {language}: {len(uncached)} uncached entries")
    print(f"Catalog: {output_path.resolve()}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Translate Delta game menu catalogs")
    sub = parser.add_subparsers(dest="command", required=True)

    translate = sub.add_parser("translate", help="Translate a menu catalog with DeepL")
    translate.add_argument("catalog", type=Path)
    translate.add_argument("output", type=Path)
    translate.add_argument("--target-lang", required=True, choices=["RU", "EN"])
    translate.add_argument("--cache", type=Path, required=True)
    translate.add_argument("--api-url", default="https://api-free.deepl.com/v2/translate")
    translate.add_argument("--retries", type=int, default=5)
    translate.add_argument("--api-key-stdin", action="store_true", help=argparse.SUPPRESS)
    # The window creates this file when Stop is pressed. It is passed to every
    # step that can call DeepL, so this parser has to accept it even though the
    # menu catalog is small enough that a run rarely lasts long enough to stop.
    translate.add_argument("--cancel-file", type=Path, help=argparse.SUPPRESS)

    args = parser.parse_args()
    try:
        if args.command == "translate":
            api_key = sys.stdin.readline().rstrip("\r\n") if args.api_key_stdin else ""
            translate_catalog(
                args.catalog, args.output, args.target_lang,
                args.cache, args.api_url, args.retries, api_key,
                args.cancel_file,
            )
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        # check_cancelled raises this when the window's Stop button lands.
        sys.exit("Interrupted")
