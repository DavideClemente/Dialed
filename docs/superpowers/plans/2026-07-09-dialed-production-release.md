# Dialed Production Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebrand `AudioMixerWin` to **Dialed** and make the repo publish-ready with a full README, a PolyForm Noncommercial license, and a GitHub Actions workflow that builds a downloadable Inno Setup installer `.exe` on every `v*` tag.

**Architecture:** A one-shot mechanical+behavioral rename (exact-token sweep + two file renames) lands first so everything downstream references `Dialed`. Then release infrastructure is added: an `installer/Dialed.iss` Inno Setup script that wraps the self-contained x64 publish folder into `Dialed-Setup-<version>-x64.exe`, and a `.github/workflows/release.yml` that publishes, compiles the installer, and attaches it to a GitHub Release. Docs (README, LICENSE) and repo housekeeping round it out.

**Tech Stack:** .NET 8 / WinUI 3 / Windows App SDK (self-contained), Inno Setup 6.3+, GitHub Actions (`windows-latest`), PolyForm Noncommercial 1.0.0.

## Global Constraints

- **Project name:** `Dialed` (was `AudioMixerWin`). No live code, XAML, manifest, or runtime string may contain `AudioMixerWin` after Task 1.
- **Platform:** x64 only for releases. `-p:Platform` MUST always be passed (there is no AnyCPU).
- **Target framework:** `net8.0-windows10.0.19041.0`. Publish output path: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\`.
- **Self-contained:** builds/publishes require a `RuntimeIdentifier` (`win-x64`). Never enable trimming or single-file.
- **Icon file** `Assets\AudioMixer.ico` keeps its filename — do NOT rename it.
- **No test project exists.** Verification is build success, repo-wide search, and artifact inspection — not unit tests.
- **License:** PolyForm Noncommercial 1.0.0 (source-available; non-commercial use/contribution allowed, selling prohibited).
- **No settings migration:** the rename orphans any pre-existing `%LOCALAPPDATA%\AudioMixerWin` data and Run-key entry. This is intentional (pre-release, no public users).
- **Commit style:** each task ends in a commit; sign commits with the `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>` trailer if committing as the agent.

---

### Task 1: Rename `AudioMixerWin` → `Dialed` (mechanical + behavioral)

**Files:**
- Rename: `AudioMixerWin.csproj` → `Dialed.csproj`
- Rename: `AudioMixerWin.slnx` → `Dialed.slnx`
- Modify (token sweep): every tracked `*.cs`, `*.xaml`, `*.csproj`, `*.slnx`, `*.json`, `*.manifest`, `*.appxmanifest` containing `AudioMixerWin`
- Modify (explicit): `App.xaml.cs:35` (crash-log filename)
- Modify: `CLAUDE.md` (build commands / project-name references)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: a solution that builds as `Dialed` with output `Dialed.exe`; the `%LOCALAPPDATA%\Dialed` data folder and `Dialed` Run-key value name; a `Dialed.csproj` path other tasks reference.

- [ ] **Step 1: Rename the project and solution files (preserve git history)**

```bash
git mv AudioMixerWin.csproj Dialed.csproj
git mv AudioMixerWin.slnx Dialed.slnx
```

- [ ] **Step 2: Sweep the exact token `AudioMixerWin` → `Dialed` across all tracked text files**

This single replace covers namespaces, `using` directives, XAML `x:Class`/`xmlns`, the window `Title`, `app.manifest` assembly identity, `Package.appxmanifest` DisplayName/Description, `launchSettings.json` profile names, the `.slnx` project reference, and the behavioral literals (`SettingsService`/`IconStore`/`IdleGifLibraryService` `%LOCALAPPDATA%\AudioMixerWin` paths and `StartupService.ValueName`). It does NOT match `AudioMixer.ico` or `audiomixer_crash.log` (different substrings), which is intended.

Run (Git Bash):

```bash
git ls-files '*.cs' '*.xaml' '*.csproj' '*.slnx' '*.json' '*.manifest' '*.appxmanifest' \
  | xargs grep -l 'AudioMixerWin' \
  | while read -r f; do sed -i 's/AudioMixerWin/Dialed/g' "$f"; done
```

- [ ] **Step 3: Rename the crash-log filename for brand consistency**

In `App.xaml.cs`, change the temp crash-log name (the sweep does not touch it):

```csharp
                var path = Path.Combine(Path.GetTempPath(), "dialed_crash.log");
```

(was `"audiomixer_crash.log"`)

- [ ] **Step 4: Update `CLAUDE.md` project-name and build-command references**

Replace occurrences of `AudioMixerWin.csproj` with `Dialed.csproj` and the project/app name `AudioMixerWin` with `Dialed` in `CLAUDE.md` prose and command examples (e.g. the build line becomes `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`). Leave historical spec files under `docs/superpowers/specs/` untouched.

```bash
sed -i 's/AudioMixerWin\.csproj/Dialed.csproj/g; s/AudioMixerWin/Dialed/g' CLAUDE.md
```

- [ ] **Step 5: Delete stale build output so regenerated XAML code-behind uses the new namespace**

```bash
rm -rf bin obj
```

- [ ] **Step 6: Verify no `AudioMixerWin` remains in live files**

Run:

```bash
git ls-files | grep -v '^docs/superpowers/specs/' | xargs grep -l 'AudioMixerWin' || echo "CLEAN"
```

Expected: prints `CLEAN` (only the historical spec docs may still contain the old name, and they are excluded).

- [ ] **Step 7: Verify the solution still builds under the new name**

Run:

```bash
dotnet build Dialed.csproj -p:Platform=x64 -c Debug
```

Expected: `Build succeeded.` with 0 errors. (Warnings are acceptable.)

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Rename project AudioMixerWin -> Dialed"
```

---

### Task 2: Add the PolyForm Noncommercial 1.0.0 LICENSE

**Files:**
- Create: `LICENSE`

**Interfaces:**
- Consumes: nothing.
- Produces: a `LICENSE` file the README links to.

- [ ] **Step 1: Fetch the canonical license text**

Download the official plain-text license to `LICENSE`:

```bash
curl -fsSL https://polyformproject.org/wp-content/uploads/2020/06/PolyForm-Noncommercial-1.0.0.txt -o LICENSE
```

If that URL is unavailable, copy the text from the canonical page `https://polyformproject.org/licenses/noncommercial/1.0.0/` (body text starting with `# PolyForm Noncommercial License 1.0.0`).

- [ ] **Step 2: Verify the file looks right**

Run:

```bash
head -n 3 LICENSE
```

Expected: the first line reads `# PolyForm Noncommercial License 1.0.0` (or `PolyForm Noncommercial License 1.0.0`), and the file is non-empty (~2 KB).

- [ ] **Step 3: Commit**

```bash
git add LICENSE
git commit -m "Add PolyForm Noncommercial 1.0.0 license"
```

---

### Task 3: Add the Inno Setup installer script

**Files:**
- Create: `installer/Dialed.iss`
- Modify: `.gitignore` (ignore `installer/Output/`)

**Interfaces:**
- Consumes: the self-contained publish folder at `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish` and the icon `Assets\AudioMixer.ico`.
- Produces: `installer/Output/Dialed-Setup-<AppVersion>-x64.exe`. Compiler defines the workflow passes: `AppVersion`, `PublishDir`.

- [ ] **Step 1: Create `installer/Dialed.iss`**

```iss
; Inno Setup script for Dialed. Requires Inno Setup 6.3+ (for x64compatible).
; Compile locally:
;   iscc /DAppVersion=1.0.0 "/DPublishDir=<abs path to publish>" installer\Dialed.iss
; PublishDir defaults to the local Release x64 publish output when not supplied.

#define AppName "Dialed"
#define AppPublisher "Davide Clemente"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
#endif

#ifndef OutputDir
  #define OutputDir "Output"
#endif

[Setup]
AppId={{7B4F0C2E-3A6D-4E51-9B2A-1D9E6C8F0A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Dialed.exe
OutputDir={#OutputDir}
OutputBaseFilename=Dialed-Setup-{#AppVersion}-x64
SetupIconFile=..\Assets\AudioMixer.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Dialed.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Dialed.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Dialed.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
```

- [ ] **Step 2: Ignore the installer output directory**

Append to `.gitignore`:

```gitignore

# Inno Setup installer output
installer/Output/
```

- [ ] **Step 3: Produce a publish folder to test against**

Run:

```bash
dotnet publish Dialed.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```

Expected: `Published to .../win-x64/publish/` and `Dialed.exe` present in that folder:

```bash
ls "bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/Dialed.exe"
```

Expected: the path prints (file exists).

- [ ] **Step 4: Compile the installer if Inno Setup is available (else defer to CI)**

If `iscc` is installed (`ISCC.exe`, typically `C:\Program Files (x86)\Inno Setup 6\`):

```bash
"/c/Program Files (x86)/Inno Setup 6/ISCC.exe" //DAppVersion=1.0.0 installer/Dialed.iss
```

Expected: `Successful compile` and `installer/Output/Dialed-Setup-1.0.0-x64.exe` exists. If `iscc` is not installed, skip this step — Task 4's workflow verifies compilation in CI.

- [ ] **Step 5: Commit**

```bash
git add installer/Dialed.iss .gitignore
git commit -m "Add Inno Setup installer script for Dialed"
```

---

### Task 4: Add the GitHub Actions release workflow

**Files:**
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: `Dialed.csproj`, the `win-x64` publish profile, `installer/Dialed.iss`.
- Produces: on a `v*` tag, a GitHub Release with `Dialed-Setup-<version>-x64.exe` attached; on `workflow_dispatch`, the installer as a build artifact (no release).

- [ ] **Step 1: Create `.github/workflows/release.yml`**

```yaml
name: Release

on:
  push:
    tags:
      - 'v*'
  workflow_dispatch:
    inputs:
      version:
        description: 'Version for a manual test build (no GitHub Release is published)'
        required: false
        default: '0.0.0-test'

permissions:
  contents: write

jobs:
  release:
    runs-on: windows-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Derive version
        id: version
        shell: pwsh
        run: |
          if ('${{ github.ref_type }}' -eq 'tag') {
            $v = '${{ github.ref_name }}' -replace '^v', ''
          } else {
            $v = '${{ github.event.inputs.version }}'
          }
          "version=$v" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          Write-Host "Building version $v"

      - name: Publish (x64, self-contained)
        run: dotnet publish Dialed.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64 -p:Version=${{ steps.version.outputs.version }}

      - name: Install Inno Setup
        run: choco install innosetup -y --no-progress

      - name: Build installer
        shell: pwsh
        run: |
          $publish = Resolve-Path 'bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
          & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" `
            "/DAppVersion=${{ steps.version.outputs.version }}" `
            "/DPublishDir=$publish" `
            installer\Dialed.iss

      - name: Upload build artifact
        uses: actions/upload-artifact@v4
        with:
          name: Dialed-Setup
          path: installer/Output/*.exe

      - name: Create GitHub Release
        if: github.ref_type == 'tag'
        uses: softprops/action-gh-release@v2
        with:
          files: installer/Output/*.exe
          generate_release_notes: true
```

- [ ] **Step 2: Validate the YAML parses**

Run (Git Bash; uses Python which ships on the runner but check locally if available):

```bash
python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml')); print('YAML OK')"
```

Expected: `YAML OK`. If Python/pyyaml is unavailable locally, visually confirm indentation is consistent (2 spaces) and there are no tabs.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "Add GitHub Actions release workflow (tag -> installer)"
```

---

### Task 5: Write the full project README

**Files:**
- Create: `README.md`
- Create: `docs/images/.gitkeep` (placeholder folder for screenshots)

**Interfaces:**
- Consumes: `LICENSE` (Task 2), `Arduino/README.md`, `CLAUDE.md`, the Releases page produced by Task 4.
- Produces: the repo's public entry point.

- [ ] **Step 1: Create the screenshots placeholder folder**

```bash
mkdir -p docs/images
touch docs/images/.gitkeep
```

- [ ] **Step 2: Create `README.md`**

```markdown
# Dialed

> A hardware-controlled, per-application volume mixer for Windows.

Dialed pairs a small physical controller (an ESP32/Arduino with knobs or rotary
encoders, a push switch, and a round display) with a Windows desktop app so you can
set the volume of individual apps — Spotify, a game, your call — by turning a real
knob, mute with a press, and flip your default output device with a switch. The
controller's round display shows the app icon and volume of whatever you're adjusting.

<!-- Add screenshots to docs/images/ and reference them here, e.g.: -->
<!-- ![Dialed mixer window](docs/images/mixer.png) -->

## Features

- Per-app **and** master volume control from physical knobs / rotary encoders
- Mute an app with a knob press
- Switch the Windows default output device from a hardware switch (or in-app)
- Live app icons + volume mirrored to the controller's round GC9A01 display
- Idle-screen GIF library uploaded to the controller's flash
- Runs from the system tray; optional start-with-Windows
- English and Portuguese (PT-PT) localization

## Hardware

You need the companion controller: an ESP32 (or Arduino Nano variant) with
potentiometers or rotary encoders, a push switch, and a round GC9A01 display,
connected over USB serial. Wiring, the serial protocol, and the firmware sketches
live in **[`Arduino/README.md`](Arduino/README.md)**.

## Installation

1. Go to the [**Releases**](../../releases) page.
2. Download the latest `Dialed-Setup-<version>-x64.exe`.
3. Run it and follow the installer.

> **SmartScreen note:** the installer is not code-signed, so Windows SmartScreen may
> show *"Windows protected your PC"*. Click **More info → Run anyway** to proceed.
> (Code signing requires a paid certificate and isn't set up for this project.)

Requires 64-bit Windows 10 version 1809 (build 17763) or newer.

## Usage

1. Plug in the controller over USB.
2. Launch Dialed; open **Settings** and pick the controller's COM port.
3. On the mixer page, assign an app to each knob.
4. Turn a knob to set that app's volume; press to mute; use the switch to change the
   output device.

## Build from source

Prerequisites: **.NET 8 SDK** and the **Windows App SDK** tooling (Visual Studio 2022
with the "Windows App SDK" / WinUI workload, or the standalone SDK).

```powershell
dotnet build Dialed.csproj -p:Platform=x64 -c Debug
```

`-p:Platform` is **required** — there is no `AnyCPU` platform (valid values: `x64`,
`x86`, `ARM64`). The app is self-contained, so builds bundle the Windows App SDK
runtime and need a matching `RuntimeIdentifier` (derived automatically from the
platform).

To produce a release publish folder:

```powershell
dotnet publish Dialed.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```

## Architecture

Dialed is a single MVVM WinUI 3 app. NAudio's Core Audio API applies per-app volumes
to Windows audio sessions; a symmetric, stringly-typed serial protocol talks to the
controller firmware. For a full tour of the code, see
**[`CLAUDE.md`](CLAUDE.md)**.

## Releasing

Push a version tag and GitHub Actions builds the installer and publishes a Release:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The [`Release`](.github/workflows/release.yml) workflow publishes the x64
self-contained build, compiles the Inno Setup installer, and attaches
`Dialed-Setup-<version>-x64.exe` to the GitHub Release.

## Contributing

Contributions are welcome — issues and pull requests both. By contributing you agree
your changes are licensed under the project's non-commercial license (below).

## License

Licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)**. In short: you
may use, modify, and share Dialed for any **non-commercial** purpose, but you may not
sell it or use it for commercial advantage. See [`LICENSE`](LICENSE) for the full
terms.
```

- [ ] **Step 3: Verify referenced files exist so no README link is dead**

Run:

```bash
ls LICENSE Arduino/README.md CLAUDE.md .github/workflows/release.yml docs/images/.gitkeep
```

Expected: all five paths print (all exist).

- [ ] **Step 4: Commit**

```bash
git add README.md docs/images/.gitkeep
git commit -m "Add project README and screenshots placeholder"
```

---

### Task 6: Repo housekeeping — untrack local MSIX test artifacts

**Files:**
- Modify: `.gitignore`
- Untrack: `AppPackages/`, `BundleArtifacts/` (kept on disk)

**Interfaces:**
- Consumes: nothing.
- Produces: a clean public repo without local packaging artifacts.

- [ ] **Step 1: Ignore the local packaging folders**

Append to `.gitignore`:

```gitignore

# Local MSIX packaging / bundle artifacts (not for the public repo)
AppPackages/
BundleArtifacts/
```

- [ ] **Step 2: Untrack the already-committed artifacts (leave files on disk)**

```bash
git rm -r --cached AppPackages BundleArtifacts
```

Expected: git reports the `.msixbundle` files and `BundleArtifacts/x64.txt` removed from the index.

- [ ] **Step 3: Verify they are now ignored and untracked**

Run:

```bash
git status --short AppPackages BundleArtifacts
git check-ignore AppPackages BundleArtifacts
```

Expected: `git status` shows the deletions staged (`D`) and no untracked re-adds; `git check-ignore` prints both folder names (confirming they're ignored).

- [ ] **Step 4: Commit**

```bash
git add .gitignore
git commit -m "Untrack local MSIX packaging artifacts"
```

---

### Task 7: First release (`v1.0.0`) — end-to-end verification

**Files:** none (git tag + observation).

**Interfaces:**
- Consumes: everything above.
- Produces: the first public GitHub Release with a downloadable installer.

> **Gate:** this publishes a public release. Confirm with the maintainer before running, and push to the correct remote/branch first.

- [ ] **Step 1: Push all preceding commits**

```bash
git push origin master
```

- [ ] **Step 2 (optional but recommended): Dry-run the workflow without publishing**

In the GitHub UI: **Actions → Release → Run workflow** (`workflow_dispatch`), leave the
default version. Confirm the run succeeds and produces a `Dialed-Setup` build artifact.
Expected: green run; downloadable `Dialed-Setup-0.0.0-test-x64.exe` artifact.

- [ ] **Step 3: Tag and push the release**

```bash
git tag v1.0.0
git push origin v1.0.0
```

- [ ] **Step 4: Verify the Release**

In the GitHub UI (or `gh release view v1.0.0`): confirm a Release named `v1.0.0` exists
with `Dialed-Setup-1.0.0-x64.exe` attached and auto-generated notes.

- [ ] **Step 5: Verify the installer on a Windows machine**

Download the attached `.exe`, run it (accept the SmartScreen prompt via *More info →
Run anyway*), and confirm:
- installs to `C:\Program Files\Dialed`,
- a **Dialed** Start Menu shortcut launches the app,
- the app runs (tray icon appears),
- Add/Remove Programs lists **Dialed** and uninstalling removes it cleanly.

---

## Self-Review

**Spec coverage:**
- Rename (Component 0) → Task 1 (mechanical sweep + behavioral literals via the same token match + crash-log + CLAUDE.md). ✔
- Inno Setup installer (Component 1) → Task 3. ✔
- GitHub Actions workflow (Component 2) → Task 4. ✔
- README (Component 3) → Task 5. ✔
- LICENSE (Component 4) → Task 2. ✔
- Housekeeping (Component 5): `.gitignore` for `installer/Output/` → Task 3 Step 2; `AppPackages/`+`BundleArtifacts/` ignore + untrack → Task 6; CLAUDE.md rename → Task 1 Step 4; RELEASING documented in README §Releasing → Task 5. ✔
- First tag `v1.0.0` + end-to-end verification → Task 7. ✔
- GitHub repo rename (`gh repo rename Dialed`) is a maintainer follow-up noted in the spec as out-of-band; intentionally not a plan task.

**Placeholder scan:** No TBD/TODO or "add error handling" style steps; every code/command step shows concrete content. The one external fetch (LICENSE) has a canonical URL + a documented fallback and a verification check.

**Type/name consistency:** Output exe is `Dialed.exe` everywhere (installer icons, `UninstallDisplayIcon`, workflow); publish path `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish` is identical in Task 3 and Task 4; installer output name `Dialed-Setup-<version>-x64.exe` matches between the `.iss` `OutputBaseFilename` and the README/Task 7 expectations; compiler defines `AppVersion`/`PublishDir` match between the `.iss` `#ifndef`s and the workflow's `/D` flags.
