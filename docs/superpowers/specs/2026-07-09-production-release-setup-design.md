# Production Release Setup — Design

**Date:** 2026-07-09
**Status:** Approved (pending spec review)

## Goal

Make AudioMixerWin ready for public GitHub publishing: a polished README, a real
license, and downloadable release artifacts so a user can grab a single installer
`.exe` from the GitHub Releases page and install the app — no `dotnet`, no Visual
Studio, no manual dependency setup.

## Constraints & Context

- The app is a **self-contained WinUI 3 / Windows App SDK** desktop app
  (`net8.0-windows10.0.19041.0`). A self-contained build is a **folder** of files
  (app host exe + bundled Windows App SDK runtime + native DLLs), **not** a single
  loose `.exe`. Single-file publish is not reliable for WinUI 3 (native components
  can't all self-extract), so distribution wraps the publish folder.
- No AnyCPU platform exists; `-p:Platform` must always be passed. Existing publish
  profiles live in `Properties/PublishProfiles/win-{x64,x86,arm64}.pubxml`.
- The app already manages its own **"start with Windows"** setting in-app via the
  HKCU `...\CurrentVersion\Run` key (`Core/Services/StartupService.cs`, surfaced on
  `SettingsPage`). Therefore the installer must **not** offer an autostart option —
  that is the app's responsibility, and duplicating it would conflict.
- Git remote: `git@github.com:DavideClemente/AudioMixer.git`. No README, no LICENSE,
  no `.github/` today.

## Decisions

| Topic | Decision |
|-------|----------|
| Distribution format | **Inno Setup installer `.exe`** (single downloadable installer) |
| Release process | **GitHub Actions** triggered on `v*` tag push |
| Architectures | **x64 only** |
| License | **PolyForm Noncommercial 1.0.0** (source-available; non-commercial use/contribution allowed, selling prohibited) |
| README | **Full project README** covering app + hardware/firmware |
| Installer autostart option | **Excluded** — handled in-app by `StartupService` |
| Code signing | **Out of scope** — documented SmartScreen caveat instead |

## Components

### 1. Inno Setup script — `installer/AudioMixerWin.iss`

- Consumes the self-contained publish output directory and bundles its entire
  contents recursively.
- Parameterized via preprocessor defines passed on the command line:
  - `AppVersion` — release version (from the git tag; default fallback e.g. `0.0.0`).
  - `PublishDir` — path to the publish folder.
  - `OutputDir` / `OutputBaseFilename` — where and what to name the installer.
- Behavior:
  - Installs to `{autopf}\AudioMixerWin` (Program Files).
  - Stable `AppId` GUID so upgrades/uninstalls are tracked correctly.
  - Start Menu shortcut always; desktop shortcut as an **optional task** (unchecked
    by default is fine).
  - Uses `Assets\AudioMixer.ico` for the installer and shortcuts.
  - Standard uninstaller registered in Add/Remove Programs.
  - "Launch AudioMixerWin" checkbox on the final page (`postinstall nowait`).
- Output: `AudioMixerWin-Setup-<AppVersion>-x64.exe`.
- Runs identically locally (`iscc /DAppVersion=... /DPublishDir=... AudioMixerWin.iss`)
  and in CI.

### 2. GitHub Actions workflow — `.github/workflows/release.yml`

- **Trigger:** `push` on tags matching `v*`. (Optionally `workflow_dispatch` for
  manual test runs.)
- **Runner:** `windows-latest`.
- **Steps:**
  1. Checkout.
  2. `actions/setup-dotnet` with .NET 8.
  3. Derive version from tag: `v1.2.3` → `1.2.3`.
  4. `dotnet publish AudioMixerWin.csproj -c Release -p:Platform=x64
     -p:PublishProfile=win-x64 -p:Version=<version>` → publishes to
     `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\`.
  5. Install Inno Setup (`choco install innosetup -y`).
  6. Compile the `.iss` with `iscc`, passing the version and publish dir.
  7. Create/Update a GitHub Release for the tag and upload the installer `.exe`
     (via `softprops/action-gh-release`, using the default `GITHUB_TOKEN`).
- **Permissions:** `contents: write` (needed to create the release).

### 3. README.md (repo root)

Sections, in order:
1. Title + badges (latest release, license).
2. One-line pitch + short paragraph (hardware-controlled per-app volume mixer for
   Windows).
3. Screenshot placeholder (`docs/images/…`) with a note to add real screenshots.
4. **Features** (per-app + master volume, mute, hardware knob/encoder control,
   output-device switching, idle-screen GIF library, tray + startup, EN/PT).
5. **Hardware requirements** — ESP32/Arduino + GC9A01 round display + pots/encoders +
   switch; link to `Arduino/README.md` for wiring/firmware.
6. **Installation** — download the latest installer from the Releases page, run it.
   Note: the installer is **unsigned**, so Windows SmartScreen shows an "unknown
   publisher" warning → *More info → Run anyway*.
7. **Usage quickstart** — plug in the controller, pick the COM port, assign apps to
   knobs.
8. **Build from source** — prerequisites (.NET 8 SDK, Windows App SDK workload),
   `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`, note on why
   `-p:Platform` is mandatory (no AnyCPU) and the self-contained requirement.
9. **Architecture** — one paragraph + pointer to `CLAUDE.md` for the deep dive.
10. **Releasing** — push a `vX.Y.Z` tag; the workflow builds and publishes the
    installer (or a short link to `RELEASING.md`).
11. **Contributing** — brief; PRs welcome, non-commercial scope.
12. **License** — PolyForm Noncommercial 1.0.0, one plain-English sentence.

### 4. LICENSE (repo root)

- Full, verbatim **PolyForm Noncommercial 1.0.0** text, fetched from the official
  source (polyformproject.org) for accuracy — not paraphrased.

### 5. Housekeeping

- `.gitignore` additions: `installer/Output/`, `AppPackages/`, `BundleArtifacts/`.
- Untrack the currently committed MSIX test artifacts:
  `git rm -r --cached AppPackages BundleArtifacts` (files stay on disk).
- Optional `RELEASING.md` documenting the tag → release flow (or fold into README §10).

## Data / Control Flow (release)

```
maintainer: git tag v1.2.3 && git push --tags
   → GitHub Actions (release.yml) fires on tag
      → dotnet publish (Release, x64, self-contained) → publish folder
      → iscc AudioMixerWin.iss → AudioMixerWin-Setup-1.2.3-x64.exe
      → create GitHub Release "v1.2.3", attach installer
end user: Releases page → download .exe → run → installed app
```

## Error Handling / Edge Cases

- **Publish folder missing / path wrong:** `iscc` fails loudly; workflow fails — no
  release is produced. Acceptable (fail fast).
- **Tag not matching `v*`:** workflow doesn't trigger. Documented.
- **SmartScreen warning:** unavoidable without a paid code-signing cert; documented
  in README rather than worked around.
- **Version mismatch:** single source of truth is the git tag, threaded into both the
  build (`-p:Version`) and the installer filename/metadata.

## Testing / Verification

- Lint the workflow YAML (valid syntax) and confirm `iscc` compiles the `.iss` (a
  local dry run with a small/dummy publish dir if Inno Setup is available, otherwise
  verified by the first CI run on a test tag).
- Verify the produced installer installs to Program Files, creates the Start Menu
  shortcut, launches the app, and uninstalls cleanly (manual, on a real tag or a
  `workflow_dispatch` test build).
- Confirm README links resolve and badges point at the correct repo.

## Out of Scope

- Code signing / EV certificate.
- x86 and ARM64 installers.
- MSIX distribution channel (the manifest stays for local dev).
- A separate standalone local build script (the `.iss` doubles for local use).
- Auto-update mechanism.
