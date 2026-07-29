# Design: One-click ESP32 firmware flashing

**Date:** 2026-07-27
**Status:** Approved (pending spec review)
**Scope:** Add a Settings feature that flashes the connected ESP32 controller with bundled, versioned firmware, so end users who download the Dialed app never need the Arduino toolchain.

## Problem

Users download the Dialed Windows app but have no way to get firmware onto their controller board — they'd need to install the Arduino IDE / `arduino-cli`, configure TFT_eSPI, and flash manually. This is a hard barrier for a product that otherwise "just works" from the tray.

## Goals

- A single button in Settings flashes a connected **classic ESP32 (WROOM)** board with the `mixer/` firmware.
- Firmware ships **bundled** in the app (offline, version always matches the app release).
- The app can tell what firmware version is **installed** on a connected board vs. what's **available** to flash.
- The flashing layer is abstracted (`IBoardFlasher`) so other boards (Nano, ESP32-S3/C3) can be added later without rework.

## Non-goals (v1)

- Non-WROOM ESP32 variants (S3/C3), Arduino Nano, or the knobs-only sketch. The abstraction anticipates them; no implementation ships.
- Automatic firmware updates or over-the-air flashing.
- Solving auto-reset for boards that require the physical BOOT button (surfaced as guidance instead).

## Decisions (from brainstorming)

| Decision | Choice |
|---|---|
| Board scope (v1) | ESP32 WROOM only; `IBoardFlasher` abstraction for later boards |
| Firmware source | Bundled in the app as a **merged** `.bin` |
| Flash tool | Bundle Espressif's official **esptool.exe**, invoked as a subprocess |
| esptool in repo | **Committed** to `tools/esptool/` (reproducible, offline builds) |
| Chip target | Classic ESP32 (WROOM); refuse other detected chips with a clear message |
| Flash UX | `ContentDialog` with live progress + esptool log |
| UI placement | New **"Firmware"** section in `SettingsPage` |
| Version handshake | **Included** — device reports `fw:esp32-mixer:<version>` on boot + on `ver?` |
| Firmware version scheme | Independent SemVer in `version.h`, decoupled from the app version |

## Architecture

### 1. Firmware versioning — single source of truth

`Arduino/mixer/version.h` (new):

```c
#define FW_VERSION "1.0.0"
#define FW_BOARD   "esp32-mixer"
```

Everything derives from this file so source, bundled binary, and device-reported version can never drift.

- **Git**: firmware releases are tagged `firmware-v1.0.0`, independent of app tags. `FW_VERSION` is bumped by hand only when the `.ino`/modules actually change.
- Rationale for an **independent** SemVer: firmware changes far less often than the app. Reusing the app version would make every app release look like "new firmware available" even when no `.ino` changed.

### 2. Build pipeline (deliberate, not per-app-build)

`Arduino/tools/build-firmware.ps1` (new):
1. Reads `FW_VERSION` / `FW_BOARD` from `version.h`.
2. `arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer` → bootloader/partitions/app binaries.
3. `esptool merge_bin` → a single **merged image** flashable at offset `0x0`:
   `firmware/esp32/mixer-esp32-<FW_VERSION>.bin`.
4. Writes `firmware/esp32/manifest.json`:
   ```json
   { "board": "esp32-mixer", "version": "1.0.0", "bin": "mixer-esp32-1.0.0.bin", "sha256": "<hex>" }
   ```

Documented in `Arduino/README.md`. Firmware is a versioned artifact, **not** rebuilt on every `dotnet build`.

### 3. Bundled assets (csproj)

Shipped next to the exe via `<Content ... CopyToOutputDirectory="PreserveNewest">` (same mechanism as `Assets\*`):

- `tools/esptool/esptool.exe` — committed, ~10 MB.
- `firmware/esp32/mixer-esp32-<version>.bin` — the merged image.
- `firmware/esp32/manifest.json` — board + version + sha256.

`installer/Dialed.iss` must include the new `tools/` and `firmware/` folders (verify its `[Files]` globs cover them recursively).

### 4. Flasher service — `Core/Services/Firmware/`

- **`IBoardFlasher`** — the extensibility seam:
  ```csharp
  string BoardId { get; }            // "esp32-mixer"
  string DisplayName { get; }
  string FirmwareVersion { get; }    // from manifest.json
  Task FlashAsync(string comPort, IProgress<FlashProgress> progress, CancellationToken ct);
  ```
- **`Esp32Flasher : IBoardFlasher`** — the only implementation shipping now:
  1. **Detect chip**: `esptool.exe --port <COM> chip_id`. If not a classic ESP32, throw `FlashException` with a localized "unsupported chip" reason (S3/C3 not yet supported).
  2. **Flash**: `esptool.exe --port <COM> --baud 921600 write_flash 0x0 <merged.bin>`. Parse stdout `Writing at 0x… (NN %)` into `FlashProgress { Percent, StatusText }`.
  - Invoked via `System.Diagnostics.Process` with redirected stdout/stderr; cancellation kills the process.
- **`FirmwareCatalog`** — loads `manifest.json`, resolves bundled `.bin` path, lists available boards.
- **DTOs**: `FlashProgress`, `FlashResult`.
- **`FlashException`** — mirrors the existing `IdleGifUploadException` pattern: carries localized, user-readable reasons (port busy, wrong chip, esptool missing, board not in bootloader mode, timeout).

### 5. Serial-port coordination (critical correctness piece)

Flashing needs **exclusive** ownership of the COM port, but `MainViewModel` holds it open and the auto-reconnect **watchdog timer** keeps re-opening it. A new `MainViewModel.FlashControllerAsync(...)` owns the bracket so the dialog never touches serial internals:

1. Suspend the auto-reconnect watchdog.
2. `DetachSerial()` + `_serial.Stop()` — fully release the port.
3. `await flasher.FlashAsync(comPort, progress, ct)`.
4. In `finally`: re-arm the watchdog and `Reconnect()`.

Existing behavior re-syncs `assign:`/`icon:`/`cfg:` state automatically on reconnect. After reconnect the device reports `fw:` again, refreshing the installed-version readout.

### 6. Version handshake (symmetric protocol change)

Per the CLAUDE.md rule, both sides change together.

**Firmware (`Arduino/mixer/`)**:
- On boot, print `fw:esp32-mixer:<FW_VERSION>`.
- On receiving `ver?`, reply with the same line.
- Nano/knobs tiers get the same lines later when they're added (out of scope now).

**`SerialManager`**:
- Parse an inbound `fw:<board>:<version>` line into a new `FirmwareReported` event (`board`, `version`).
- Add a `RequestFirmwareVersion()` that sends `ver?`.
- **Do not** treat `fw:` like `vol:` — it must not switch the device screen.

**`MainViewModel`**:
- Handle `FirmwareReported`; expose `InstalledFirmwareVersion` (nullable) for the UI.
- Send `ver?` shortly after a successful connect (boot line usually arrives on its own; `ver?` covers the already-running case).

### 7. UI — new "Firmware" section + flash dialog

**`SettingsPage.xaml`** — new "Firmware" section (its own title + card, below Controller):
- Reads **Firmware: v1.0.0** (installed, from `InstalledFirmwareVersion`) when connected, or "Unknown / not connected".
- A **"Flash controller firmware"** button opening the dialog.
- When installed < available: an "Update available (v1.0.0 → v1.1.0)" hint.

**`FlashFirmwareDialog`** (new control) + **`FlashFirmwareViewModel`**:
- Shows target board, **bundled** firmware version, current COM port, and installed version if known.
- **Flash** button → progress bar + status line + a scrolling esptool log (reuse the diagnostics `ListView` styling).
- Requires a selected COM port; warns if none.
- Prevents closing mid-flash; **Cancel** kills the esptool process.
- Terminal states:
  - **Success** → green `InfoBar`, confirms auto-reconnect.
  - **Error** → red `InfoBar` with esptool's reason + a "hold the BOOT button and retry" hint (many bare WROOM boards can't auto-enter download mode).

### 8. Localization

All new strings added to **both** `Strings/en-US/Resources.resw` and `Strings/pt-PT/Resources.resw`, accessed via `Loc.Get(...)`. Includes section title/caption, button, dialog labels, progress states, and every `FlashException` reason.

## Error handling

`FlashException` reasons (all localized):
- No COM port selected / port busy or in use by another app.
- esptool.exe missing from the bundle (packaging/dev-build guard).
- Detected chip is not a classic ESP32.
- Board not in bootloader/download mode (timeout waiting for the ROM loader) → BOOT-button hint.
- Flash write failure / verify mismatch.
- Operation cancelled by the user.

## Testing

No test project exists in the repo, so verification is **manual** and documented here:

1. **Happy path** — connect a real WROOM, flash, confirm progress reaches 100%, board reboots, app auto-reconnects, `fw:` version shows.
2. **Version readout** — after flashing, Firmware section shows the bundled version; disconnect/reconnect still shows it.
3. **No port** — button/dialog warns cleanly, no crash.
4. **Wrong chip** — (if an S3/C3 is available) chip detection refuses with the localized message.
5. **esptool missing** — temporarily remove the binary; guarded error, no unhandled exception.
6. **Cancel** — cancel mid-flash; esptool process is killed; app re-arms serial and reconnects.
7. **Port contention** — confirm the watchdog does not re-grab the port mid-flash.

## Assumptions / risks

1. **esptool.exe committed** to `tools/esptool/` (~10 MB binary in git history) — accepted for reproducibility/offline builds.
2. **Merged `.bin` produced by a manual release script**, versioned by hand via `version.h`.
3. **BOOT-button caveat** — auto-reset isn't solved; surfaced as UI guidance.
4. **Chip scope** — a single bundled WROOM image; other ESP32 variants deferred to the `IBoardFlasher` abstraction.

## Files touched (anticipated)

**New**
- `Arduino/mixer/version.h`
- `Arduino/tools/build-firmware.ps1`
- `firmware/esp32/mixer-esp32-<version>.bin`, `firmware/esp32/manifest.json`
- `tools/esptool/esptool.exe`
- `Core/Services/Firmware/IBoardFlasher.cs`, `Esp32Flasher.cs`, `FirmwareCatalog.cs`, `FlashProgress.cs`, `FlashResult.cs`, `FlashException.cs`
- `Core/ViewModels/FlashFirmwareViewModel.cs`
- `Core/Controls/FlashFirmwareDialog.xaml(.cs)`

**Modified**
- `Arduino/mixer/mixer.ino` (emit `fw:`, handle `ver?`)
- `Arduino/README.md` (build-firmware + versioning docs)
- `Core/SerialManager.cs` (`FirmwareReported` event, `RequestFirmwareVersion`, `ver?` parse)
- `Core/ViewModels/MainViewModel.cs` (`FlashControllerAsync`, `InstalledFirmwareVersion`, `ver?` on connect)
- `Core/Views/SettingsPage.xaml(.cs)` (Firmware section)
- `Dialed.csproj` (bundle `tools/`, `firmware/`)
- `installer/Dialed.iss` (package `tools/`, `firmware/`)
- `Strings/en-US/Resources.resw`, `Strings/pt-PT/Resources.resw`
