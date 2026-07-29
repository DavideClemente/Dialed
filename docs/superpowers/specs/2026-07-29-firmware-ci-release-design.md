# Firmware CI + release injection — design

**Date:** 2026-07-29
**Status:** Approved, not yet implemented

## Problem

`Arduino/mixer` (the ESP32 firmware) is only ever compiled by hand. Two consequences:

1. **Nothing catches a broken sketch.** A commit that breaks the firmware build is invisible
   until someone happens to compile locally.
2. **Every released installer ships without firmware.** `firmware/esp32/*.bin`,
   `firmware/esp32/manifest.json` and `tools/esptool/esptool.exe` are untracked, and the csproj
   includes them conditionally (`Condition="Exists(...)"`). A clean CI checkout has none of them,
   so `dotnet publish` produces an app where `FirmwareCatalog.Esp32` returns null and the
   one-click flash UI in Settings degrades to unavailable — in *every* installer published so far.

## Goal

Compile the firmware in GitHub Actions, and on a `v*` tag bake the result into the installer
*and* attach it to the GitHub Release.

## Pinned toolchain

Taken from the maintainer's working machine, where the current shipped image was built:

| Component  | Version              |
| ---------- | -------------------- |
| esptool    | 5.3.1                |
| ESP32 core | 3.3.10               |
| TFT_eSPI   | 2.5.43               |
| FQBN       | `esp32:esp32:esp32`  |

`Arduino/mixer/User_Setup.h` must overwrite the installed TFT_eSPI's own `User_Setup.h` — the
library has no GC9A01 pin mapping otherwise and the sketch will not compile.

Only TFT_eSPI is needed. `SPI.h` and `LittleFS.h` come from the ESP32 core; `FastLED` is present
in the maintainer's library folder but is not used by `mixer`.

## Decisions

| Question                        | Decision                                                                 |
| ------------------------------- | ------------------------------------------------------------------------ |
| What the tag ships              | Firmware baked into the installer **and** attached as standalone assets  |
| Triggers                        | Separate PR/push check on `Arduino/**`, plus its own build on tag        |
| Sketches compiled               | `Arduino/mixer` only                                                     |
| Firmware version                | Independent, read from `Arduino/mixer/version.h`; no bump enforcement    |
| esptool source                  | Downloaded in CI, version pinned via `tools/esptool/VERSION.txt`         |

Rejected: committing the 14 MB `esptool.exe` (bloats every clone, contradicts the policy in
`tools/esptool/README.md`); reusing the ESP32 core's bundled esptool (version drifts with the
core and may not match the v5 hyphenated CLI syntax that `build-firmware.ps1` and `Esp32Flasher`
both depend on).

## Architecture

Three files, one of them an edit.

### 1. `.github/actions/setup-arduino/action.yml` — new, composite

Shared by both workflows so the toolchain cannot drift between the PR check and the release build.

- Install `arduino-cli`.
- `core install esp32:esp32@<core-version>`, `lib install TFT_eSPI@<lib-version>`. Both are action
  inputs defaulting to the pinned versions above.
- Copy `Arduino/mixer/User_Setup.h` over the installed TFT_eSPI's `User_Setup.h`. **Fail** if the
  target library directory does not exist, rather than silently compiling against stock settings.
- `actions/cache` over the arduino15 data directory, keyed on core version + lib version +
  hash of `Arduino/mixer/User_Setup.h`, so bumping any of them invalidates the cache.

The action is OS-agnostic and does **not** handle esptool — that is Windows-only and belongs to
the release workflow.

### 2. `.github/workflows/firmware.yml` — new

```
on:
  push / pull_request
  paths: Arduino/**, .github/workflows/firmware.yml, .github/actions/setup-arduino/**
runs-on: ubuntu-latest
```

Steps: checkout → setup-arduino → `arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer`.

Compile-only: no merge, no esptool, no artifact. Fast and free.

**Accepted tradeoff:** a regression in the *merge* step specifically (e.g. a partition-table
change that breaks the offsets) is not caught here and surfaces only at tag time. Running the
full merged build on `windows-latest` would close that gap at roughly double the runner cost.

### 3. `.github/workflows/release.yml` — edited

New steps inserted **between Checkout and Publish**. The ordering is load-bearing: MSBuild
evaluates the csproj `Content Include` globs when `dotnet publish` starts, so the generated files
must already be on disk by then.

1. **setup-arduino** (composite action above).
2. **Fetch esptool** — read the version from `tools/esptool/VERSION.txt`, download
   `esptool-v<version>-windows-amd64.zip` from Espressif's GitHub releases, extract
   `esptool.exe` to `tools/esptool/`. Fails loudly, quoting the URL attempted.
3. **Build firmware** — run the existing `Arduino/tools/build-firmware.ps1` **unchanged**. It
   already reads `version.h`, compiles, merges at `0x1000`/`0x8000`/`0xe000`/`0x10000`, and
   writes `manifest.json` with the sha256. Reusing it keeps one source of truth for the merge
   layout instead of duplicating offsets into YAML.
4. **Verify firmware assets** — assert `tools/esptool/esptool.exe`, `firmware/esp32/*.bin` and
   `firmware/esp32/manifest.json` all exist.

Then the existing chain works unmodified: the csproj copies all three into the publish directory,
and `installer/Dialed.iss` already ships `{#PublishDir}\*` with `recursesubdirs`.

5. **Verify publish output** — after `dotnet publish`, assert the three files landed in the
   publish directory.
6. **Release assets** — `softprops/action-gh-release` gains `firmware/esp32/*.bin` and
   `firmware/esp32/manifest.json` alongside the existing setup `.exe`.

The two verify steps are the most important guard in this change. A missing `esptool.exe` is
**not** a build error — the csproj's `Condition="Exists(...)"` produces a perfectly successful
build of an installer with no flasher in it. Every other failure mode in this design is loud;
this one is silent, and it is exactly the bug being fixed.

The existing `workflow_dispatch` path builds firmware too, so manual test builds are
representative. It still publishes no GitHub Release.

### 4. Housekeeping

`tools/esptool/VERSION.txt` currently holds the placeholder `esptool (not yet added — see
README.md)`. It becomes machine-readable (`5.3.1`) because CI parses it.
`tools/esptool/README.md` is updated to match, and to state that CI now fetches the exe
automatically from this pin.

`Arduino/README.md` gains a note that `FW_VERSION` in `version.h` must be bumped whenever the
sketch changes (see the risk below).

## Consequences

- **Installer grows by ~15 MB** (14 MB `esptool.exe` + ~1.3 MB merged firmware). In exchange,
  one-click flash works out of the box in released builds for the first time.
- **Unbumped `FW_VERSION` is unguarded.** Firmware versioning is independent of the app tag by
  design, so tagging `v1.4.0` may legitimately ship `esp32-mixer-1.0.0.bin`. But if the sketch
  changes without a `FW_VERSION` bump, two different binaries ship under one version string and
  the app's `fw:<board>:<version>` comparison will not detect a stale device. Documented, not
  enforced.

## Verification plan

No claim that the pipeline works will be made on the strength of the YAML alone.

1. Push the branch → `firmware.yml` proves the sketch compiles on a clean machine.
2. Run `release.yml` via `workflow_dispatch` with version `0.0.0-test` → download the
   `Dialed-Setup` artifact and confirm the installer contains `firmware\esp32\` and
   `tools\esptool\esptool.exe`.
3. Only after that run is green, cut the real tag.

## Out of scope

Firmware signing; OTA updates; a firmware-only release track independent of app tags; compiling
`mixer_nano`, `display_test` or `arduino`.
