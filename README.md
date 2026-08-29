# Delta/Route2 translation tools

Delta/Route2 visual-novel translation pipeline for Windows. Scenario text is
extracted into Excel workbooks, translated, proofread, and compiled into a
runtime overlay. The original scenario archive is not modified.

The runtime proxy is developed for `02 Reika Mesuinu Choukyou xx ni Okasarete`
and the 2007-06-07 `RSA.EXE` build (462,848 bytes). Extraction and workbook tools
can be useful for related Delta titles, but the native hook offsets and byte
signatures are executable-specific.

## Architecture

The application has three layers:

- Python 3.10+ handles scenario extraction, DeepL requests and caching,
  proofreading rules, overlay serialization, and fit reports.
- .NET Framework 4 executables provide the GUI, CLI, GARbro-backed resource
  operations, launcher generation, and native GDI line measurement.
- `winmm.dll` proxies the system WinMM library and hooks the supported game at
  runtime. It loads `delta_overlay.<language>.bin` and optional localized UI
  assets selected by `DeltaLauncher.exe`.

Scenario message windows use `D#####` identifiers. Choice/menu windows use
`C#####`. DeepL cache entries for scenario text are keyed by that window
identity, the ordered Japanese source signature, language, and translation
method. The source signature prevents a renumbered window from accidentally
reusing a translation that belongs to different source lines.

Proofreading first loads `profiles/proofread_common.<language>.json`, then the
game-specific `work/<game>/proofread_rules.<language>.json`. Project rules may
replace a source line, a row, or a complete `D`/`C` window.

## Requirements

| Operation | Requirement |
| --- | --- |
| GUI and CLI | Windows 7 SP1 or newer with .NET Framework 4 |
| Python pipeline | Python 3.10+ and `openpyxl>=3.1,<3.2` |
| DeepL translation | DeepL API key |
| Managed build | .NET Framework `csc.exe` |
| `winmm.dll` build | Visual Studio 2022, Desktop development with C++, toolset v143 |
| Runtime overlay | Supported 32-bit game executable |

Install Python dependencies with:

```powershell
python -m pip install -r requirements.txt
```

Windows is the supported environment. Wine implements the Windows DPAPI call
used for the saved DeepL token, so Proton/Wine may work, but it is not a tested
target. DPAPI data belongs to the current Windows user or Wine prefix; recreating
the prefix requires entering the token again.

The GUI passes the token to Python over standard input. The saved value in
`bin/delta_translator.settings.json` is protected with DPAPI `CurrentUser`; it
is not placed in process arguments or logs. The CLI reads `DEEPL_API_KEY` from
the environment.

## Repository layout

```text
bin/             built executables and deployed DLL dependencies
gui/             C# GUI, CLI, and resource backend
profiles/        common proofread rules and templates
py/              Python pipeline
runtime_proxy/   game-specific overlay hook
tests/           Python module tests and fixtures
vendor/          GARbro and VNTranslationTools sources/binaries
work/<game>/     workbooks, rules, cache, reports, and UI assets
outputs/         audit and analysis reports
```

`DeltaProject` derives all working paths from the tool directory, selected game
folder, and target language. A project currently uses the final game-directory
name as `work/<game>`; do not point two different installations with the same
folder name at one tool checkout.

## Build

Bootstrap a checkout and build all available components:

```bat
build.cmd
```

After `bin\delta.exe` exists:

```bat
bin\delta.exe build
bin\delta.exe build --proxy
```

`--proxy` builds `bin\winmm.dll` with MSBuild. Managed builds select the
numerically newest valid `vendor/GARbro*` directory. The current directory is
`vendor/GARbro-v2.0.0.0`; its top-level DLLs are copied to `bin` and listed in
the build output.

## GUI workflow

Run `bin\DeltaTranslator.exe` and use the tabs in order:

1. **Project setup** — choose game folder, executable, `RSAN.SD`, profile,
   language, and optional DeepL token.
2. **Extract text** — create `work/<game>/source.xlsx`.
3. **Translate** — calculate an estimate or fill
   `translation.<language>.xlsx`.
4. **Proofread** — apply common and project rules and write
   `translation.<language>.proofread.xlsx`.
5. **Menu translation** — extract, translate, and write Win32 menu maps.
6. **Build overlay** — compile the selected language workbook and generate a
   fit report.
7. **Localized UI resources** — first extract source CGF/IAF images, then build
   only the Russian and English resources explicitly added by the translator.
8. **Game launcher** — install the proxy, launcher, overlays, and available UI
   assets into the game folder.

The language selectors on Project setup, Translate, Proofread, and Build overlay
represent one shared setting.

`DeltaLauncher.exe` is a copy of the GUI executable placed beside the game. It
selects Japanese, Russian, or English and records the choice in
`delta_launcher.ini`.

## CLI workflow

`bin\delta.exe help` prints the current command syntax. Common commands are:

```bat
bin\delta.exe extract   --game "D:\Games\Game"
bin\delta.exe estimate  --game "D:\Games\Game" --lang RU
bin\delta.exe translate --game "D:\Games\Game" --lang RU
bin\delta.exe proofread --game "D:\Games\Game" --lang RU
bin\delta.exe overlay   --game "D:\Games\Game" --lang RU
bin\delta.exe pipeline  --game "D:\Games\Game" --lang RU
```

Shared options:

- `--game DIR` — game folder; defaults to the current directory.
- `--exe FILE` — executable; defaults to `RSA.EXE`.
- `--source FILE` — scenario archive; defaults to `RSAN.SD`.
- `--lang RU|EN` — target language; defaults to RU.
- `--generic` — disable game-specific assumptions.
- `--tools DIR` — override the tool checkout directory.

Translation options:

- `--overwrite` ignores reusable translations.
- `--max-dialogs N` limits the number of submitted windows.
- `--loop-retries N` retries a looped DeepL result line by line. The default is
  `0`; enable it explicitly only when the additional API use is acceptable.

Overlay option `--strict-fit` makes overflowing text a build failure. Without
it, overflows are warnings and are written to
`work/<game>/delta_overlay.<language>.fit.tsv`.

The CLI does not install the game launcher. Use GUI tab 8 for installation.

## Work files

| File | Purpose |
| --- | --- |
| `source.xlsx` | extracted Japanese source and dialog identifiers |
| `translation.ru.xlsx`, `translation.en.xlsx` | machine/manual translation |
| `translation.<lang>.proofread.xlsx` | output of the rule-driven proofread |
| `proofread_rules.<lang>.json` | game-specific corrections |
| `deepl_cache.jsonl` | translation cache |
| `deepl_unresolved_loops.<lang>.json` | unresolved DeepL loops for one language |
| `delta_overlay.<lang>.fit.tsv` | lines exceeding the configured message frame |
| `ui_assets/` | extracted Japanese UI images and their explicit RU/EN variants |

`translation.<lang>.proofread.xlsx` is preferred for overlay builds when it
exists. If the raw translation is newer, both front ends report that the
proofread output is stale; review that warning before building.

### Localized UI resources

The first action on tab 7 scans `CG` for base `.CGF` archives and loose `.IAF`
images. Files whose stem already ends in `.jp`, `.ru`, or `.en` are language
switching copies, not additional sources, and are ignored during discovery. If
a `.jp` companion already exists, extraction reads that Japanese backup instead
of the possibly switched active file.

Archive contents are written below `work/<game>/ui_assets/<archive>/`; loose
images are written below `work/<game>/ui_assets/_loose/`. Each editable source
has a `NAME.jp.png` preview and its `NAME.jp.IAF` container. Extraction is
deliberately broad: it decodes every IAF GARbro accepts and does not try to
detect whether an image contains text.

Keep the source dimensions unchanged, edit a `NAME.jp.png`, and save the result
beside it as `NAME.ru.png` and/or `NAME.en.png`. The build action recursively
considers only those explicit language suffixes (an advanced workflow may
provide `.ru.IAF`/`.en.IAF` directly). A localized CGF is emitted only when at
least one matching translated image exists in that archive. Loose translated
images become loose localized IAF files. Japanese backup files are created in
the game only for resources that actually have a localized variant.

At launch, the language picker switches the union of resources for which a
`.ru` or `.en` variant exists. If one resource has no variant for the selected
language, that resource falls back to its `.jp` copy; unrelated CGF and IAF
files are never touched.

## Warnings and diagnostics

Proofread prints the current unresolved-loop count. If loops remain, the CLI
writes a `WARNING:` line and the GUI keeps the warning visible on the Proofread
tab after completion.

The private overlay codepage replaces unsupported characters with `?`. Such a
build completes but emits a `WARNING:` line in the CLI and leaves a warning on
the GUI Build overlay tab. Correct the text or extend the codepage before
shipping that overlay.

Diagnostic files:

- `bin/delta_translator.log` — GUI commands, subprocess output, exit state, and
  managed exceptions.
- `<game>/log/proxy.log` — proxy startup, real WinMM loading and exports,
  executable identity/signatures, overlay parsing, hook transactions, window
  hooks, dialogs, shutdown exceptions, and translation counters.
- `<game>/log/untranslated.log` — unique untranslated runtime strings when
  `LOG_UNTRANSLATED=1` in `delta_launcher.ini`.

For a useful bug report, include `proxy.log`, `delta_translator.log`, the exact
game executable size/hash, selected language, overlay file, and the operation
that triggered the problem. Do not include the settings file or DeepL token.

Supported `[Overlay]` settings in `delta_launcher.ini`:

```ini
TEXT_X=32
FONT_HEIGHT=20
LETTER_SPACING=0
LOG_UNTRANSLATED=0
```

Invalid values are ignored and recorded in `proxy.log`.

## Tests

Tests use `unittest`. Run only the module affected by a change, for example:

```powershell
python -m unittest tests.proofreading.test_proofread_rules
python -m unittest tests.overlay.test_overlay_format
python -m unittest tests.translate.test_choice_jobs
```

The normal suite uses the small resources committed under `tests/fixtures` and
does not inspect adjacent game or `work` folders. Real-game integration checks
are opt-in through `DELTA_TEST_GAME`.

Native proxy changes are verified with `bin\delta.exe build --proxy`. A live
game smoke test is still required before distributing a runtime DLL.

## Third-party code

| Component | Use | License/source record |
| --- | --- | --- |
| GARbro (`vendor/GARbro-v2.0.0.0`) | CGF archives and IAF images | MIT; `LICENSE.GARbro` |
| VNTranslationTools / VNTextProxy | WinMM proxy and rendering hooks | MIT; `LICENSE.VNTranslationTools` |
| Microsoft Detours | native hook transactions | vendored with VNTranslationTools |

Review the corresponding license files before redistribution.
