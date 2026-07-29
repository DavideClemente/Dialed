# Firmware CI + Release Injection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compile `Arduino/mixer` in GitHub Actions on every change, and on a `v*` tag bake the merged ESP32 image plus `esptool.exe` into the Windows installer and attach the image to the GitHub Release.

**Architecture:** A composite action pins the Arduino toolchain and is shared by two workflows, so the PR check and the release build can never drift. A new `firmware.yml` runs a compile-only check on Linux. `release.yml` gains firmware steps *between* checkout and `dotnet publish` — the existing csproj `Content` globs and the installer's `recursesubdirs` then carry the files into the installer with no change to either.

**Tech Stack:** GitHub Actions (composite action, `arduino/setup-arduino-cli@v2`, `actions/cache@v4`, `softprops/action-gh-release@v2`), arduino-cli, PowerShell 7 (`pwsh` — present on both `ubuntu-latest` and `windows-latest` runners), Inno Setup, .NET 8.

**Spec:** `docs/superpowers/specs/2026-07-29-firmware-ci-release-design.md`

## Global Constraints

- Pinned toolchain, exact values: ESP32 core **`3.3.10`**, TFT_eSPI **`2.5.43`**, esptool **`5.3.1`**, FQBN **`esp32:esp32:esp32`**.
- `Arduino/mixer/User_Setup.h` MUST overwrite the installed TFT_eSPI's own `User_Setup.h`. Without it the sketch does not compile — the stock library has no GC9A01 pin mapping.
- Only `Arduino/mixer` is compiled. `mixer_nano`, `display_test` and `arduino` are out of scope.
- `Arduino/mixer/version.h` (`FW_VERSION`) stays the sole source of truth for the firmware version. Never rewrite it from the git tag.
- `Arduino/tools/build-firmware.ps1` MUST be reused unchanged. Do not duplicate the flash offsets (`0x1000`/`0x8000`/`0xe000`/`0x10000`) into YAML.
- All new shell steps use `shell: pwsh` and fail loudly (`throw`) — a missing artifact must never pass silently, because the csproj's `Condition="Exists(...)"` will happily build an installer without it.
- Runtime paths the app resolves against `AppContext.BaseDirectory` (see `Core/Services/Firmware/FirmwareCatalog.cs:18-24`) — these are what the verification steps assert:
  - `tools/esptool/esptool.exe`
  - `firmware/esp32/manifest.json`
  - `firmware/esp32/<manifest.bin>`, whose SHA-256 must equal `manifest.sha256`
- There is no test project in this repo. Verification is by real workflow runs and by inspecting produced artifacts — never by asserting the YAML "looks right".

---

## File Structure

| File | Status | Responsibility |
| --- | --- | --- |
| `.github/actions/setup-arduino/action.yml` | Create | Installs arduino-cli + pinned core/library, applies `User_Setup.h`, caches. The single definition of the toolchain. |
| `.github/workflows/firmware.yml` | Create | Compile-only check on `Arduino/**` changes. Linux, fast. |
| `.github/workflows/release.yml` | Modify | Adds firmware build + two verification gates + release assets. |
| `tools/esptool/VERSION.txt` | Modify | Becomes machine-readable (`5.3.1`); parsed by `release.yml`. |
| `tools/esptool/README.md` | Modify | Documents the new pin format and that CI fetches the exe. |
| `Arduino/README.md` | Modify | Notes that `FW_VERSION` must be bumped when the sketch changes. |

Unchanged on purpose: `Dialed.csproj`, `installer/Dialed.iss`, `Arduino/tools/build-firmware.ps1`.

---

### Task 1: Composite toolchain action + firmware compile check

**Files:**
- Create: `.github/actions/setup-arduino/action.yml`
- Create: `.github/workflows/firmware.yml`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: a composite action referenced as `uses: ./.github/actions/setup-arduino`, with inputs `core-version` (default `3.3.10`) and `tft-espi-version` (default `2.5.43`), and outputs `data-dir` (arduino-cli data directory) and `user-dir` (arduino-cli sketchbook directory, where `libraries/TFT_eSPI` lives). Task 2 uses this action with default inputs and does not read its outputs.

The composite action and the workflow ship together because a composite action has no independently testable deliverable — the workflow run *is* its test.

- [ ] **Step 1: Create the composite action**

Create `.github/actions/setup-arduino/action.yml`:

```yaml
name: Setup Arduino toolchain
description: >
  Installs arduino-cli with the pinned ESP32 core and TFT_eSPI, then overwrites the
  library's User_Setup.h with Arduino/mixer/User_Setup.h so the GC9A01 pin mapping
  matches the hardware. Shared by firmware.yml and release.yml so the compile check
  and the released binary are built with the same toolchain.

inputs:
  core-version:
    description: ESP32 Arduino core version.
    required: false
    default: '3.3.10'
  tft-espi-version:
    description: TFT_eSPI library version.
    required: false
    default: '2.5.43'

outputs:
  data-dir:
    description: arduino-cli data directory (installed cores live here).
    value: ${{ steps.dirs.outputs.data }}
  user-dir:
    description: arduino-cli sketchbook directory (installed libraries live here).
    value: ${{ steps.dirs.outputs.user }}

runs:
  using: composite
  steps:
    - name: Install arduino-cli
      uses: arduino/setup-arduino-cli@v2

    - name: Initialise config and resolve directories
      id: dirs
      shell: pwsh
      run: |
        arduino-cli config init --overwrite | Out-Null
        $data = (arduino-cli config get directories.data).Trim()
        $user = (arduino-cli config get directories.user).Trim()
        if (-not $data -or -not $user) { throw "Could not resolve arduino-cli directories" }
        "data=$data" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
        "user=$user" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
        Write-Host "data=$data"
        Write-Host "user=$user"

    # Restored before the installs so a hit turns them into no-ops. The key covers
    # both pinned versions and User_Setup.h, so bumping any of them busts the cache.
    - name: Cache cores and libraries
      uses: actions/cache@v4
      with:
        path: |
          ${{ steps.dirs.outputs.data }}
          ${{ steps.dirs.outputs.user }}
        key: arduino-${{ runner.os }}-core${{ inputs.core-version }}-tft${{ inputs.tft-espi-version }}-${{ hashFiles('Arduino/mixer/User_Setup.h') }}

    - name: Install ESP32 core
      shell: pwsh
      env:
        ESP32_INDEX: https://espressif.github.io/arduino-esp32/package_esp32_index.json
      run: |
        $corePath = Join-Path '${{ steps.dirs.outputs.data }}' 'packages/esp32/hardware/esp32/${{ inputs.core-version }}'
        if (Test-Path $corePath) {
          Write-Host "esp32 core ${{ inputs.core-version }} already present (cache hit)"
        } else {
          arduino-cli core update-index --additional-urls $env:ESP32_INDEX
          if ($LASTEXITCODE -ne 0) { throw "arduino-cli core update-index failed" }
          arduino-cli core install esp32:esp32@${{ inputs.core-version }} --additional-urls $env:ESP32_INDEX
          if ($LASTEXITCODE -ne 0) { throw "arduino-cli core install failed" }
        }
        if (-not (Test-Path $corePath)) { throw "esp32 core not found at $corePath after install" }

    - name: Install TFT_eSPI
      shell: pwsh
      run: |
        $libDir = Join-Path '${{ steps.dirs.outputs.user }}' 'libraries/TFT_eSPI'
        if (Test-Path $libDir) {
          Write-Host "TFT_eSPI already present (cache hit)"
        } else {
          arduino-cli lib install "TFT_eSPI@${{ inputs.tft-espi-version }}"
          if ($LASTEXITCODE -ne 0) { throw "arduino-cli lib install failed" }
        }
        if (-not (Test-Path $libDir)) { throw "TFT_eSPI not found at $libDir after install" }

    # The stock library has no GC9A01 pin mapping; without this the sketch does not
    # compile. Mirrors the manual step documented in Arduino/README.md.
    - name: Apply the sketch's User_Setup.h to TFT_eSPI
      shell: pwsh
      run: |
        $target = Join-Path '${{ steps.dirs.outputs.user }}' 'libraries/TFT_eSPI/User_Setup.h'
        $source = Join-Path $env:GITHUB_WORKSPACE 'Arduino/mixer/User_Setup.h'
        if (-not (Test-Path $source)) { throw "Missing $source" }
        Copy-Item -Path $source -Destination $target -Force
        Write-Host "Applied User_Setup.h -> $target"
```

- [ ] **Step 2: Create the compile-check workflow**

Create `.github/workflows/firmware.yml`:

```yaml
name: Firmware

on:
  push:
    paths:
      - 'Arduino/**'
      - '.github/workflows/firmware.yml'
      - '.github/actions/setup-arduino/**'
  pull_request:
    paths:
      - 'Arduino/**'
      - '.github/workflows/firmware.yml'
      - '.github/actions/setup-arduino/**'
  workflow_dispatch:

jobs:
  compile:
    name: Compile mixer (ESP32)
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup Arduino toolchain
        uses: ./.github/actions/setup-arduino

      # Compile only. The merge into a flashable image needs the Windows esptool
      # and happens in release.yml.
      - name: Compile mixer sketch
        shell: pwsh
        run: |
          arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer
          if ($LASTEXITCODE -ne 0) { throw "arduino-cli compile failed" }
```

- [ ] **Step 3: Commit and push so CI actually runs it**

```bash
git add .github/actions/setup-arduino/action.yml .github/workflows/firmware.yml
git commit -m "ci: compile the ESP32 mixer firmware on Arduino/ changes"
git push -u origin firmware-ci-release
```

- [ ] **Step 4: Verify the run is green — do not skip, do not infer**

```bash
gh run watch --exit-status $(gh run list --workflow=firmware.yml --limit 1 --json databaseId --jq '.[0].databaseId')
```

Expected: the `Compile mixer (ESP32)` job succeeds, and the log shows arduino-cli's `Sketch uses NNNNNN bytes` summary.

If it fails, read the log before changing anything. The two likely causes:
- `TFT_eSPI.h: No such file or directory` → the lib install or the sketchbook path is wrong; check the `data=`/`user=` values printed by the *Initialise config* step.
- `GC9A01_DRIVER` / pin errors, or `Sketch uses` never appearing → `User_Setup.h` was not applied; check the *Apply the sketch's User_Setup.h* step's printed target path.

- [ ] **Step 5: Confirm the cache works on a second run**

Re-run the workflow:

```bash
gh workflow run firmware.yml --ref firmware-ci-release
```

Expected: the *Install ESP32 core* step prints `esp32 core 3.3.10 already present (cache hit)` and the job is materially faster than the first run.

---

### Task 2: Pin esptool and build the firmware into the release

**Files:**
- Modify: `tools/esptool/VERSION.txt` (currently the placeholder `esptool (not yet added — see README.md)`)
- Modify: `tools/esptool/README.md`
- Modify: `.github/workflows/release.yml:20-42` (insert steps after *Checkout*, and one after *Publish*)

**Interfaces:**
- Consumes: `./.github/actions/setup-arduino` from Task 1, with default inputs.
- Produces: on-disk, before `dotnet publish` runs — `tools/esptool/esptool.exe`, `firmware/esp32/<board>-<FW_VERSION>.bin`, `firmware/esp32/manifest.json`. Task 3 attaches the latter two to the Release.

- [ ] **Step 1: Make VERSION.txt machine-readable**

Replace the entire contents of `tools/esptool/VERSION.txt` with exactly one line:

```
5.3.1
```

(No `v` prefix, no `esptool ` prefix — `release.yml` validates this against `^\d+\.\d+\.\d+$`.)

- [ ] **Step 2: Update the esptool README to match**

In `tools/esptool/README.md`, replace step 3 of the setup instructions and add a CI note. The file currently says `Record the version you downloaded in tools/esptool/VERSION.txt, e.g. esptool v5.3.1.` — that example format is now wrong. Change it to:

```markdown
3. Record the version you downloaded in `tools/esptool/VERSION.txt` as a bare
   SemVer on a single line, e.g. `5.3.1` — no `v` prefix. The release workflow
   parses this file to download the matching `esptool.exe`, so the format is
   load-bearing.

## CI

`.github/workflows/release.yml` downloads
`esptool-v<VERSION.txt>-windows-amd64.zip` from Espressif's GitHub releases on
every release build, so the exe does not need to be committed. Bumping the pin
is a one-line edit to `VERSION.txt` — do it here and locally at the same time so
CI and your machine stay on the same version.
```

- [ ] **Step 3: Add the firmware steps to release.yml**

In `.github/workflows/release.yml`, insert these three steps **immediately after the `Checkout` step and before `Setup .NET`**. The position matters: MSBuild evaluates the csproj's `Content Include` globs when `dotnet publish` starts, so the files must exist on disk by then.

```yaml
      - name: Setup Arduino toolchain
        uses: ./.github/actions/setup-arduino

      # esptool.exe is not committed (14 MB); the pin lives in VERSION.txt so CI
      # and the maintainer's machine stay on the same version. It is needed twice:
      # by build-firmware.ps1 to merge the image, and at runtime by Esp32Flasher.
      - name: Fetch pinned esptool
        shell: pwsh
        run: |
          $version = (Get-Content 'tools/esptool/VERSION.txt' -Raw).Trim()
          if ($version -notmatch '^\d+\.\d+\.\d+$') {
            throw "tools/esptool/VERSION.txt must hold a bare SemVer (e.g. 5.3.1); got '$version'"
          }
          $url = "https://github.com/espressif/esptool/releases/download/v$version/esptool-v$version-windows-amd64.zip"
          $zip = Join-Path $env:RUNNER_TEMP 'esptool.zip'
          Write-Host "Downloading $url"
          try {
            Invoke-WebRequest -Uri $url -OutFile $zip
          } catch {
            throw "esptool download failed for $url : $_"
          }
          $extract = Join-Path $env:RUNNER_TEMP 'esptool-extract'
          Expand-Archive -Path $zip -DestinationPath $extract -Force
          $exe = Get-ChildItem -Path $extract -Recurse -Filter 'esptool.exe' | Select-Object -First 1
          if (-not $exe) { throw "esptool.exe not found inside $url" }
          Copy-Item $exe.FullName 'tools/esptool/esptool.exe' -Force
          & 'tools/esptool/esptool.exe' version

      # Reuses the maintainer's script unchanged so the flash offsets and the
      # manifest schema have exactly one definition.
      - name: Build ESP32 firmware
        shell: pwsh
        run: ./Arduino/tools/build-firmware.ps1
```

- [ ] **Step 4: Add the pre-publish verification gate**

Immediately after the `Build ESP32 firmware` step, add:

```yaml
      # A missing asset is NOT a build error: the csproj includes these
      # conditionally, so publish would succeed and ship an installer with no
      # flasher in it. Fail here instead.
      - name: Verify firmware assets
        shell: pwsh
        run: |
          foreach ($f in @('tools/esptool/esptool.exe', 'firmware/esp32/manifest.json')) {
            if (-not (Test-Path $f)) { throw "Missing expected firmware asset: $f" }
          }
          $manifest = Get-Content 'firmware/esp32/manifest.json' -Raw | ConvertFrom-Json
          $bin = Join-Path 'firmware/esp32' $manifest.bin
          if (-not (Test-Path $bin)) { throw "manifest.json names '$($manifest.bin)' but it does not exist" }
          Write-Host "Firmware: $($manifest.board) $($manifest.version) -> $($manifest.bin)"
```

- [ ] **Step 5: Add the post-publish verification gate**

Insert **after** the `Publish (x64, self-contained)` step and **before** `Install Inno Setup`:

```yaml
      # Mirrors exactly what FirmwareCatalog.Esp32 checks at runtime (paths +
      # sha256): if this passes, the shipped app will offer one-click flashing.
      - name: Verify firmware in publish output
        shell: pwsh
        run: |
          $publish = 'bin/Release/net8.0-windows10.0.19041.0/win-x64/publish'
          $esptool = Join-Path $publish 'tools/esptool/esptool.exe'
          $manifestPath = Join-Path $publish 'firmware/esp32/manifest.json'
          foreach ($f in @($esptool, $manifestPath)) {
            if (-not (Test-Path $f)) { throw "Missing from publish output: $f" }
          }
          $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
          $bin = Join-Path $publish "firmware/esp32/$($manifest.bin)"
          if (-not (Test-Path $bin)) { throw "Missing from publish output: $bin" }
          $sha = (Get-FileHash -Algorithm SHA256 -Path $bin).Hash.ToLowerInvariant()
          if ($sha -ne $manifest.sha256) {
            throw "sha256 mismatch in publish output: manifest $($manifest.sha256), file $sha"
          }
          Write-Host "Publish output carries $($manifest.bin) (sha256 verified)"
```

- [ ] **Step 6: Commit**

```bash
git add tools/esptool/VERSION.txt tools/esptool/README.md .github/workflows/release.yml
git commit -m "ci: build and bundle the ESP32 firmware in release builds"
git push
```

- [ ] **Step 7: Run the release workflow manually and verify the artifact**

```bash
gh workflow run release.yml --ref firmware-ci-release -f version=0.0.0-test
```

Then watch it:

```bash
gh run watch --exit-status $(gh run list --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
```

Expected: green, no `Create GitHub Release` step runs (it is gated on `github.ref_type == 'tag'`), and the *Verify firmware in publish output* log line reads `Publish output carries esp32-mixer-1.0.0.bin (sha256 verified)`.

- [ ] **Step 8: Confirm the installer really contains the firmware**

Download and inspect the built installer — a green workflow is not sufficient evidence that the payload is inside the setup exe.

```bash
gh run download $(gh run list --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId') -n Dialed-Setup -D /tmp/dialed-setup
```

Then, from PowerShell, list the packed files (Inno Setup's own extractor is not installed; use `innoextract` if available, otherwise install the setup into a scratch directory with `/VERYSILENT /DIR=`):

```powershell
& (Get-ChildItem /tmp/dialed-setup/*.exe)[0].FullName /VERYSILENT /DIR=C:\dialed-verify | Out-Null
Get-ChildItem C:\dialed-verify\firmware\esp32, C:\dialed-verify\tools\esptool
```

Expected: `manifest.json`, `esp32-mixer-1.0.0.bin`, and `esptool.exe` are all present. Remove `C:\dialed-verify` afterwards.

Record the actual observed output in the task report. If any file is missing, the bug is in the csproj `Content` globs or the `.iss` `recursesubdirs` flag, not in the workflow — investigate before proceeding to Task 3.

---

### Task 3: Attach firmware to the GitHub Release

**Files:**
- Modify: `.github/workflows/release.yml` (the `Upload build artifact` and `Create GitHub Release` steps at the end)
- Modify: `Arduino/README.md`

**Interfaces:**
- Consumes: `firmware/esp32/*.bin` and `firmware/esp32/manifest.json` produced by Task 2.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Publish the firmware as its own build artifact**

In `.github/workflows/release.yml`, immediately after the existing `Upload build artifact` step, add:

```yaml
      - name: Upload firmware artifact
        uses: actions/upload-artifact@v4
        with:
          name: Dialed-Firmware
          path: |
            firmware/esp32/*.bin
            firmware/esp32/manifest.json
```

This makes the image downloadable from `workflow_dispatch` test runs too, not only from tags.

- [ ] **Step 2: Attach the firmware to the Release**

Replace the `files:` value of the existing `Create GitHub Release` step. It currently reads:

```yaml
        with:
          files: installer/Output/*.exe
          generate_release_notes: true
```

Change it to:

```yaml
        with:
          files: |
            installer/Output/*.exe
            firmware/esp32/*.bin
            firmware/esp32/manifest.json
          generate_release_notes: true
```

- [ ] **Step 3: Document the FW_VERSION bump requirement**

The design deliberately does not enforce this, so it must be written down. Add to `Arduino/README.md`, in the `mixer/` section:

```markdown
### Versioning

`mixer/version.h` holds `FW_VERSION`, the single source of truth for the firmware
version. **Bump it (SemVer) in the same commit as any change under `mixer/`.**

It is deliberately independent of the app's `v*` release tag — tagging `v1.4.0`
may legitimately ship `esp32-mixer-1.0.0.bin` if the firmware did not change.
Nothing enforces the bump: if you change the sketch without bumping, two
different binaries ship under one version string and the app's
`fw:<board>:<version>` check will not notice a connected device is stale.
```

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/release.yml Arduino/README.md
git commit -m "ci: attach the ESP32 firmware image to tagged releases"
git push
```

- [ ] **Step 5: Verify the artifact upload on a dispatch run**

```bash
gh workflow run release.yml --ref firmware-ci-release -f version=0.0.0-test
gh run watch --exit-status $(gh run list --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
gh run download $(gh run list --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId') -n Dialed-Firmware -D /tmp/dialed-fw
ls /tmp/dialed-fw
```

Expected: `esp32-mixer-1.0.0.bin` and `manifest.json`.

The Release-asset attachment itself (Step 2) cannot be exercised without pushing a real tag — it is gated on `github.ref_type == 'tag'`. Do **not** claim it works. Report it as implemented-but-unverified, and state plainly that the first real tag is its first execution.

---

## Merge and first real tag

Not a task — the operator's call after Task 3 is reviewed.

1. Open a PR from `firmware-ci-release` to `master`; confirm the `Firmware` check runs on it.
2. Merge.
3. Cut the tag. The first tagged run is the first execution of the Release-asset step; check the Release page afterwards for the setup `.exe`, the `.bin`, and `manifest.json`.

## Rollback

Every change is additive to CI plus three doc/config edits. Reverting the three commits restores the previous behaviour exactly: the installer builds without firmware and the flash UI degrades gracefully, as it does today.
