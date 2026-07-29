# ESP32 Firmware Flashing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Settings feature that flashes a connected classic ESP32 (WROOM) controller with bundled, versioned `mixer/` firmware, so users never need the Arduino toolchain.

**Architecture:** Ship Espressif's `esptool.exe` and a pre-built merged `.bin` next to the app; an `IBoardFlasher`/`Esp32Flasher` service shells out to esptool and streams progress. A new `MainViewModel.FlashControllerAsync` brackets the flash by suspending the auto-reconnect watchdog and releasing the COM port. Firmware gains a `fw:<board>:<version>` handshake so the app shows installed-vs-bundled versions.

**Tech Stack:** .NET 8 / WinUI 3, CommunityToolkit.Mvvm, `System.Diagnostics.Process`, `System.Text.Json`, Arduino/ESP32 C++, `arduino-cli` + `esptool`.

## Global Constraints

- Target framework `net8.0-windows10.0.19041.0`; platforms `x86;x64;ARM64` — **no AnyCPU**. Always pass `-p:Platform` when building: `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`.
- `Nullable` is `enable` — annotate reference types.
- **All user-facing strings** go through `Loc.Get(...)` with entries in **both** `Strings/en-US/Resources.resw` and `Strings/pt-PT/Resources.resw`.
- **Serial protocol is symmetric** — any wire change updates both `Core/SerialManager.cs` and `Arduino/mixer/`. Knob ids are 1-based on the wire.
- **Never echo `vol:`** on connect/assign; likewise the new `fw:`/`ver?` lines must not switch the device screen.
- Settings persist on change via `SettingsService.Save`; no explicit save button.
- Firmware version is an **independent SemVer** in `Arduino/mixer/version.h`, decoupled from the app version.
- Chip scope: **classic ESP32 (WROOM) only**. A merged `.bin` flashes at offset `0x0`.

## Testing approach (deviation from default TDD — read first)

This repo has **no test project**, and the new code is dominated by hardware I/O (`SerialPort`, `esptool` subprocess), WinUI dialogs, and Arduino C++ — none unit-testable without standing up test infrastructure the approved spec deliberately excluded (spec §Testing: "verification is **manual**"). Standing up an xUnit host against a self-contained WinUI 3 target is high-friction and out of scope.

Therefore each task below ends with a **concrete, observable verification step** (compile succeeds, app runs, a value appears in the Diagnostics serial log, a real board flashes) instead of a red/green unit test. Where a task contains pure parsing logic, its verification includes exact input→output examples the implementer confirms by eye or with a scratch snippet. Keep parsing logic in `static` methods so it stays trivially checkable.

---

## File Structure

**New files**
- `Arduino/mixer/version.h` — `FW_VERSION` / `FW_BOARD` defines (single source of truth).
- `Arduino/tools/build-firmware.ps1` — compiles `mixer/`, produces merged `.bin` + `manifest.json`.
- `tools/esptool/esptool.exe` — committed Espressif standalone binary.
- `firmware/esp32/mixer-esp32-<version>.bin` — merged image (build output, committed).
- `firmware/esp32/manifest.json` — `{ board, version, bin, sha256 }`.
- `Core/Services/Firmware/FirmwareManifest.cs` — manifest record + loader + sha256 verify.
- `Core/Services/Firmware/FlashProgress.cs` — progress DTO.
- `Core/Services/Firmware/FlashException.cs` — localized failure carrier.
- `Core/Services/Firmware/IBoardFlasher.cs` — flasher abstraction.
- `Core/Services/Firmware/Esp32Flasher.cs` — esptool-backed implementation.
- `Core/Services/Firmware/FirmwareCatalog.cs` — resolves bundled assets → flashers.
- `Core/ViewModels/FlashFirmwareViewModel.cs` — dialog VM.
- `Core/Controls/FlashFirmwareDialog.xaml` / `.xaml.cs` — the flash dialog.

**Modified files**
- `Arduino/mixer/mixer.ino` — emit `fw:` on boot, handle `ver?`.
- `Arduino/README.md` — versioning + build-firmware docs.
- `Core/SerialManager.cs` — `FirmwareReported` event, `RequestFirmwareVersion`, `fw:` parse.
- `Core/ViewModels/MainViewModel.cs` — `InstalledFirmwareVersion`, `BundledFirmwareVersion`, `FlashControllerAsync`, wire handshake.
- `Core/Views/SettingsPage.xaml` / `.xaml.cs` — Firmware section + dialog launch.
- `Dialed.csproj` — bundle `tools/` and `firmware/`.
- `Strings/en-US/Resources.resw`, `Strings/pt-PT/Resources.resw` — new strings.

---

## Task 1: Firmware version handshake (`fw:` / `ver?`)

**Files:**
- Create: `Arduino/mixer/version.h`
- Modify: `Arduino/mixer/mixer.ino` (add `handleVerLine`, boot print in `setup()`, dispatch in `readIncomingSerial`)

**Interfaces:**
- Produces (on the wire): board prints `fw:esp32-mixer:1.0.0` on boot and in reply to a `ver?` line. Consumed by `SerialManager` in Task 4.

- [ ] **Step 1: Create `Arduino/mixer/version.h`**

```c
#pragma once

// Single source of truth for firmware version. Bump FW_VERSION (SemVer) whenever
// the mixer/ firmware changes; the build script (Arduino/tools/build-firmware.ps1)
// reads these to name the merged .bin and fill manifest.json, and the app compares
// this (reported over serial as "fw:<board>:<version>") against the bundled version.
#define FW_BOARD   "esp32-mixer"
#define FW_VERSION "1.0.0"
```

- [ ] **Step 2: Include the header and emit the boot line in `mixer.ino`**

Add the include at the top of `Arduino/mixer/mixer.ino` (after the existing includes):

```c
#include "idlegif.h"
#include "version.h"
```

Add a helper near the other `handle…Line` functions:

```c
static void sendFirmwareVersion() {
  // Symmetric with SerialManager: "fw:<board>:<version>". Does NOT touch the
  // display/idle state — it's metadata, not a knob event.
  Serial.print("fw:");
  Serial.print(FW_BOARD);
  Serial.print(':');
  Serial.println(FW_VERSION);
}

static void handleVerLine(const char* line) {
  if (strcmp(line, "ver?") == 0)
    sendFirmwareVersion();
}
```

- [ ] **Step 3: Dispatch `ver?` in `readIncomingSerial` and announce on boot**

In `readIncomingSerial`, add `handleVerLine(inLine);` to the non-GIF branch:

```c
        if (!idleGifHandleLine(inLine)) {
          handleAssignLine(inLine);
          handleIconLine(inLine);
          handleVolumeLine(inLine);
          handleMuteLine(inLine);
          handleConfigLine(inLine);
          handleVerLine(inLine);
        }
```

In `setup()`, after `knobsSetup(...)` (which calls `Serial.begin`), announce the version:

```c
void setup() {
  displaySetup();
  knobsSetup(onKnobChange);   // knobsSetup calls Serial.begin(921600)
  sendFirmwareVersion();      // announce firmware version to the PC on boot
  lastKnobActivity = millis();
}
```

- [ ] **Step 4: Verify it compiles**

Run: `arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer`
Expected: compiles with no errors; sketch/global memory report printed.

- [ ] **Step 5: Verify on hardware (manual)**

Flash the sketch (or wait until Task 5 can flash it), open a serial monitor at 921600, reset the board.
Expected: a `fw:esp32-mixer:1.0.0` line appears on boot; sending `ver?\n` produces another `fw:esp32-mixer:1.0.0`. The display shows no volume/idle change from these lines.

- [ ] **Step 6: Commit**

```bash
git add Arduino/mixer/version.h Arduino/mixer/mixer.ino
git commit -m "firmware: report fw:<board>:<version> on boot and ver? request"
```

---

## Task 2: Build script, bundled artifacts, and csproj bundling

**Files:**
- Create: `Arduino/tools/build-firmware.ps1`
- Create (build output, committed): `firmware/esp32/mixer-esp32-1.0.0.bin`, `firmware/esp32/manifest.json`
- Add (committed binary): `tools/esptool/esptool.exe`
- Modify: `Dialed.csproj` (Content includes)
- Modify: `Arduino/README.md` (docs)

**Interfaces:**
- Produces (on disk, next to the exe after build): `firmware/esp32/manifest.json` and the merged `.bin`; `tools/esptool/esptool.exe`. Consumed by `FirmwareManifest`/`FirmwareCatalog`/`Esp32Flasher` (Tasks 3, 5).
- `manifest.json` shape: `{ "board": "esp32-mixer", "version": "1.0.0", "bin": "mixer-esp32-1.0.0.bin", "sha256": "<lowercase-hex>" }`.

- [ ] **Step 1: Add `esptool.exe` to the repo**

Download the Windows standalone build from Espressif's esptool releases (`https://github.com/espressif/esptool/releases`, the `esptool-vX.Y-windows-amd64.zip`) and place `esptool.exe` at `tools/esptool/esptool.exe`. Record the version in `tools/esptool/VERSION.txt` (a one-line file, e.g. `esptool v4.8.1`).

Verify: `tools/esptool/esptool.exe version` prints a version banner.

- [ ] **Step 2: Create `Arduino/tools/build-firmware.ps1`**

```powershell
#requires -version 7
# Builds the mixer/ firmware into a single merged image flashable at offset 0x0,
# and writes firmware/esp32/manifest.json. Version/board are read from
# Arduino/mixer/version.h so the .bin name, manifest, and device-reported version
# can never drift. Run this by hand when the firmware changes; it is NOT part of
# the dotnet build.
$ErrorActionPreference = 'Stop'

$repo      = Resolve-Path "$PSScriptRoot\..\.."
$sketch    = Join-Path $repo 'Arduino\mixer'
$versionH  = Join-Path $sketch 'version.h'
$esptool   = Join-Path $repo 'tools\esptool\esptool.exe'
$outDir    = Join-Path $repo 'firmware\esp32'
$buildDir  = Join-Path $env:TEMP 'dialed-fw-build'

function Read-Define([string]$name) {
  $line = Select-String -Path $versionH -Pattern "#define\s+$name\s+`"([^`"]+)`"" | Select-Object -First 1
  if (-not $line) { throw "Could not find #define $name in $versionH" }
  return $line.Matches[0].Groups[1].Value
}

$board   = Read-Define 'FW_BOARD'
$version = Read-Define 'FW_VERSION'
Write-Host "Building $board $version"

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
New-Item -ItemType Directory -Force -Path $outDir   | Out-Null

# 1. Compile -> bootloader/partitions/app binaries in $buildDir
arduino-cli compile --fqbn esp32:esp32:esp32 --output-dir $buildDir $sketch
if ($LASTEXITCODE -ne 0) { throw "arduino-cli compile failed" }

$appBin  = Join-Path $buildDir 'mixer.ino.bin'
$bootBin = Join-Path $buildDir 'mixer.ino.bootloader.bin'
$partBin = Join-Path $buildDir 'mixer.ino.partitions.bin'
foreach ($f in @($appBin,$bootBin,$partBin)) {
  if (-not (Test-Path $f)) { throw "Expected build artifact missing: $f" }
}

# boot_app0.bin ships with the ESP32 Arduino core. Locate it under the arduino-cli data dir.
$dataDir = (arduino-cli config get directories.data).Trim()
$bootApp0 = Get-ChildItem -Path $dataDir -Recurse -Filter 'boot_app0.bin' `
            -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $bootApp0) { throw "boot_app0.bin not found under $dataDir (install esp32 core)" }

# 2. Merge into one image (classic ESP32 offsets; merge_bin pads from 0x0)
$binName = "$board-$version.bin"
$mergedBin = Join-Path $outDir $binName
& $esptool --chip esp32 merge_bin -o $mergedBin `
  --flash_mode dio --flash_freq 40m --flash_size 4MB `
  0x1000 $bootBin 0x8000 $partBin 0xe000 $bootApp0.FullName 0x10000 $appBin
if ($LASTEXITCODE -ne 0) { throw "esptool merge_bin failed" }

# 3. Write manifest.json (sha256 of the merged bin)
$sha = (Get-FileHash -Algorithm SHA256 -Path $mergedBin).Hash.ToLowerInvariant()
$manifest = [ordered]@{ board = $board; version = $version; bin = $binName; sha256 = $sha }
$manifestPath = Join-Path $outDir 'manifest.json'
$manifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host "Wrote $mergedBin"
Write-Host "Wrote $manifestPath (sha256 $sha)"
```

- [ ] **Step 3: Run the build script to produce the artifacts**

Run: `pwsh Arduino/tools/build-firmware.ps1`
Expected: `firmware/esp32/mixer-esp32-1.0.0.bin` and `firmware/esp32/manifest.json` created; manifest `version` is `1.0.0` and `sha256` matches `(Get-FileHash firmware/esp32/mixer-esp32-1.0.0.bin).Hash`.

- [ ] **Step 4: Bundle the assets in `Dialed.csproj`**

Add to the existing `<ItemGroup>` that includes `Assets\…` (after the `Assets\*.png` line):

```xml
    <!--
      Firmware flashing assets must land next to the exe in build AND publish
      output. FirmwareCatalog/Esp32Flasher resolve them via AppContext.BaseDirectory
      at runtime. The installer picks them up automatically (recursesubdirs).
    -->
    <Content Include="tools\esptool\esptool.exe" CopyToOutputDirectory="PreserveNewest">
      <Link>tools\esptool\esptool.exe</Link>
    </Content>
    <Content Include="firmware\esp32\*" CopyToOutputDirectory="PreserveNewest">
      <Link>firmware\esp32\%(Filename)%(Extension)</Link>
    </Content>
```

- [ ] **Step 5: Verify the assets land in build output**

Run: `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`
Then confirm these exist under `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\`:
- `tools\esptool\esptool.exe`
- `firmware\esp32\manifest.json`
- `firmware\esp32\mixer-esp32-1.0.0.bin`

Expected: all three present. (The installer's `Source: "{#PublishDir}\*"; Flags: recursesubdirs` in `installer/Dialed.iss` then packages them with no `.iss` change — confirm by reading that line; do not edit it.)

- [ ] **Step 6: Document in `Arduino/README.md`**

Under the `mixer/` section, add a "Firmware versioning & bundled image" subsection: the version lives in `Arduino/mixer/version.h`; run `pwsh Arduino/tools/build-firmware.ps1` after bumping it to regenerate `firmware/esp32/*`; tag releases `firmware-vX.Y.Z`. Keep it to ~8 lines, matching the file's existing terse style.

- [ ] **Step 7: Commit**

```bash
git add tools/esptool Arduino/tools/build-firmware.ps1 firmware/esp32 Dialed.csproj Arduino/README.md
git commit -m "build: bundle esptool + merged ESP32 firmware and manifest"
```

---

## Task 3: Firmware manifest loader + catalog

**Files:**
- Create: `Core/Services/Firmware/FirmwareManifest.cs`
- Create: `Core/Services/Firmware/FirmwareCatalog.cs`

**Interfaces:**
- Consumes: `firmware/esp32/manifest.json` from Task 2.
- Produces:
  - `FirmwareManifest` record: `string Board`, `string Version`, `string Bin`, `string Sha256`; static `FirmwareManifest? TryLoad(string manifestPath)`.
  - `FirmwareCatalog`: ctor `FirmwareCatalog(string baseDir)`; `IBoardFlasher? Esp32 { get; }` (null if assets missing); `static string DefaultBaseDir => AppContext.BaseDirectory`. (Returns `IBoardFlasher` from Task 5 — see note.)

> Ordering note: `FirmwareCatalog` references `IBoardFlasher`/`Esp32Flasher` from Task 5. Implement the manifest here; add the catalog's flasher wiring at the end of Task 5. In this task, `FirmwareCatalog` exposes only `Esp32Manifest`/paths; Task 5 adds the `Esp32` flasher property.

- [ ] **Step 1: Create `FirmwareManifest.cs`**

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// The bundled firmware descriptor (firmware/&lt;board&gt;/manifest.json), produced by
/// Arduino/tools/build-firmware.ps1. Version/board/sha256 are derived from
/// Arduino/mixer/version.h + the merged .bin, so they never drift from the device.
/// </summary>
public sealed record FirmwareManifest(
    [property: JsonPropertyName("board")] string Board,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("bin")] string Bin,
    [property: JsonPropertyName("sha256")] string Sha256)
{
    /// <summary>Loads and validates a manifest, or returns null if missing/malformed.</summary>
    public static FirmwareManifest? TryLoad(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            var json = File.ReadAllText(manifestPath);
            var m = JsonSerializer.Deserialize<FirmwareManifest>(json);
            if (m is null || string.IsNullOrWhiteSpace(m.Board) ||
                string.IsNullOrWhiteSpace(m.Version) || string.IsNullOrWhiteSpace(m.Bin))
                return null;
            return m;
        }
        catch { return null; }
    }

    /// <summary>True if the file at <paramref name="binPath"/> matches this manifest's sha256.</summary>
    public bool VerifyBin(string binPath)
    {
        try
        {
            if (!File.Exists(binPath)) return false;
            using var stream = File.OpenRead(binPath);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(hash, Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
```

- [ ] **Step 2: Create `FirmwareCatalog.cs` (manifest/paths only for now)**

```csharp
using System;
using System.IO;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// Resolves the bundled flashing assets (esptool + per-board firmware) relative to
/// the running exe. The Esp32 flasher property is added in the Esp32Flasher task.
/// </summary>
public sealed class FirmwareCatalog
{
    private readonly string _baseDir;

    public FirmwareCatalog(string baseDir) => _baseDir = baseDir;

    public static string DefaultBaseDir => AppContext.BaseDirectory;

    public string EsptoolPath => Path.Combine(_baseDir, "tools", "esptool", "esptool.exe");

    public string Esp32ManifestPath => Path.Combine(_baseDir, "firmware", "esp32", "manifest.json");

    public FirmwareManifest? Esp32Manifest => FirmwareManifest.TryLoad(Esp32ManifestPath);

    public string Esp32BinPath(FirmwareManifest m) => Path.Combine(_baseDir, "firmware", "esp32", m.Bin);
}
```

- [ ] **Step 3: Verify manifest loads (scratch check)**

From a scratch snippet or the debugger, confirm with the real files from Task 2:
- `FirmwareManifest.TryLoad("<baseDir>/firmware/esp32/manifest.json")` returns a record with `Version == "1.0.0"`, `Board == "esp32-mixer"`.
- `.VerifyBin("<baseDir>/firmware/esp32/mixer-esp32-1.0.0.bin")` returns `true`.
- `TryLoad("<nonexistent>")` returns `null` (no throw).

Expected: all four hold.

- [ ] **Step 4: Commit**

```bash
git add Core/Services/Firmware/FirmwareManifest.cs Core/Services/Firmware/FirmwareCatalog.cs
git commit -m "feat: firmware manifest loader + asset catalog"
```

---

## Task 4: SerialManager firmware handshake parsing

**Files:**
- Modify: `Core/SerialManager.cs`

**Interfaces:**
- Consumes: inbound `fw:<board>:<version>` lines from firmware (Task 1).
- Produces:
  - `event Action<string, string>? FirmwareReported` — `(board, version)`, raised on the serial thread.
  - `void RequestFirmwareVersion()` — writes `ver?` if the port is open.

- [ ] **Step 1: Add the event and request method**

In `Core/SerialManager.cs`, add the event next to the other `public event` declarations (near `SwitchChanged`):

```csharp
    // The controller reports its firmware as "fw:<board>:<version>" on boot and in
    // reply to "ver?". Metadata only — handlers must not touch the device screen.
    public event Action<string, string>? FirmwareReported;
```

Add the request method next to `SendIdleTimeout`/the other senders:

```csharp
    /// <summary>Asks the controller to (re)report its firmware version via a "fw:" line.</summary>
    public void RequestFirmwareVersion()
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine("ver?"); }
        catch { }
    }
```

- [ ] **Step 2: Parse `fw:` before the 2-part split in `HandleCommand`**

In `HandleCommand`, add this branch immediately after the existing `gif:` branch and **before** `var parts = cmd.Split(':');` (the `fw:` payload has three colon-separated parts, so it must not reach the `parts.Length != 2` guard):

```csharp
        // "fw:<board>:<version>" — firmware version report. Three parts, so handle
        // before the generic 2-part knob split below.
        if (cmd.StartsWith("fw:", StringComparison.Ordinal))
        {
            var fw = cmd.Split(':');
            if (fw.Length == 3)
                FirmwareReported?.Invoke(fw[1].Trim(), fw[2].Trim());
            return;
        }
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`
Expected: build succeeds.

- [ ] **Step 4: Verify parsing (scratch check)**

Confirm by inspection that feeding `HandleCommand`-equivalent input `"fw:esp32-mixer:1.0.0"` would invoke `FirmwareReported("esp32-mixer", "1.0.0")`, and that `"fw:garbage"` (2 parts) raises nothing and does not fall through to the knob parser. (Full end-to-end observation happens in Task 6 via the Diagnostics log.)

- [ ] **Step 5: Commit**

```bash
git add Core/SerialManager.cs
git commit -m "feat: parse fw: handshake and add RequestFirmwareVersion"
```

---

## Task 5: ESP32 flasher service

**Files:**
- Create: `Core/Services/Firmware/FlashProgress.cs`
- Create: `Core/Services/Firmware/FlashException.cs`
- Create: `Core/Services/Firmware/IBoardFlasher.cs`
- Create: `Core/Services/Firmware/Esp32Flasher.cs`
- Modify: `Core/Services/Firmware/FirmwareCatalog.cs` (add `Esp32` flasher property)
- Modify: `Strings/en-US/Resources.resw`, `Strings/pt-PT/Resources.resw` (flash error/progress strings)

**Interfaces:**
- Consumes: `FirmwareCatalog` (Task 3), esptool + bin (Task 2).
- Produces:
  - `readonly record struct FlashProgress(int Percent, string StatusText)`.
  - `sealed class FlashException : Exception` (message is user-readable, localized).
  - `interface IBoardFlasher { string BoardId; string DisplayName; string FirmwareVersion; Task FlashAsync(string comPort, IProgress<FlashProgress> progress, CancellationToken ct); }`.
  - `Esp32Flasher : IBoardFlasher`.
  - `FirmwareCatalog.Esp32` → `IBoardFlasher?`.

- [ ] **Step 1: Create `FlashProgress.cs`**

```csharp
namespace Dialed.Core.Services.Firmware;

/// <summary>A flashing progress update: 0..100 percent plus a localized status line.</summary>
public readonly record struct FlashProgress(int Percent, string StatusText);
```

- [ ] **Step 2: Create `FlashException.cs`**

```csharp
using System;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// Carries a user-readable, localized reason a flash failed (mirrors the
/// IdleGifUploadException pattern). The message is meant to be shown verbatim.
/// </summary>
public sealed class FlashException : Exception
{
    public FlashException(string message) : base(message) { }
}
```

- [ ] **Step 3: Create `IBoardFlasher.cs`**

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// Flashes one board type. The extensibility seam for adding Nano / other ESP32
/// variants later; only Esp32Flasher ships today.
/// </summary>
public interface IBoardFlasher
{
    string BoardId { get; }          // e.g. "esp32-mixer"
    string DisplayName { get; }      // e.g. "ESP32 (round display)"
    string FirmwareVersion { get; }  // bundled version, from the manifest

    /// <summary>
    /// Flashes the bundled firmware to the board on <paramref name="comPort"/>.
    /// Throws <see cref="FlashException"/> with a localized reason on failure.
    /// The caller must have released the serial port first.
    /// </summary>
    Task FlashAsync(string comPort, IProgress<FlashProgress> progress, CancellationToken ct);
}
```

- [ ] **Step 4: Create `Esp32Flasher.cs`**

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// Flashes a classic ESP32 (WROOM) by shelling out to the bundled esptool.exe.
/// The merged image is written at offset 0x0. --chip esp32 makes esptool refuse a
/// non-classic ESP32 (S3/C3/…) with a "Wrong chip" style error we surface clearly.
/// </summary>
public sealed partial class Esp32Flasher : IBoardFlasher
{
    private readonly string _esptoolPath;
    private readonly string _binPath;
    private const int FlashBaud = 921600;

    public string BoardId { get; }
    public string DisplayName { get; }
    public string FirmwareVersion { get; }

    public Esp32Flasher(FirmwareManifest manifest, string esptoolPath, string binPath)
    {
        BoardId = manifest.Board;
        FirmwareVersion = manifest.Version;
        DisplayName = Loc.Get("Flash_Board_Esp32");
        _esptoolPath = esptoolPath;
        _binPath = binPath;
    }

    [GeneratedRegex(@"\((\d+)\s*%\)")]
    private static partial Regex ProgressRegex();

    /// <summary>Extracts a 0..100 percent from an esptool "Writing at 0x… (NN %)" line, or -1.</summary>
    internal static int ParsePercent(string line)
    {
        var m = ProgressRegex().Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? Math.Clamp(p, 0, 100) : -1;
    }

    /// <summary>Maps an esptool exit/output to a localized FlashException, or null on success.</summary>
    internal static string? ClassifyFailure(int exitCode, string output)
    {
        if (exitCode == 0) return null;
        var o = output.ToLowerInvariant();
        if (o.Contains("wrong chip") || o.Contains("this chip is") || o.Contains("chip is not"))
            return Loc.Get("Flash_Err_WrongChip");
        if (o.Contains("failed to connect") || o.Contains("wrong boot mode") ||
            o.Contains("no serial data received") || o.Contains("invalid head of packet"))
            return Loc.Get("Flash_Err_NotBootloader");
        if (o.Contains("access is denied") || o.Contains("could not open") || o.Contains("permission denied"))
            return Loc.Get("Flash_Err_PortBusy");
        return Loc.Get("Flash_Err_WriteFailed", output.Trim());
    }

    public async Task FlashAsync(string comPort, IProgress<FlashProgress> progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(comPort))
            throw new FlashException(Loc.Get("Flash_Err_NoPort"));
        if (!File.Exists(_esptoolPath))
            throw new FlashException(Loc.Get("Flash_Err_EsptoolMissing"));
        if (!File.Exists(_binPath))
            throw new FlashException(Loc.Get("Flash_Err_BinMissing"));

        progress.Report(new FlashProgress(0, Loc.Get("Flash_Progress_Detecting")));

        var args = $"--chip esp32 --port {comPort} --baud {FlashBaud} " +
                   $"--before default_reset --after hard_reset " +
                   $"write_flash 0x0 \"{_binPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = _esptoolPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();

        void OnLine(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            var pct = ParsePercent(line);
            if (pct >= 0)
                progress.Report(new FlashProgress(pct, Loc.Get("Flash_Progress_Writing", pct)));
        }

        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new FlashException(Loc.Get("Flash_Err_WriteFailed", ex.Message));
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw new FlashException(Loc.Get("Flash_Err_Cancelled"));
        }

        string outText;
        lock (output) outText = output.ToString();

        var failure = ClassifyFailure(proc.ExitCode, outText);
        if (failure is not null)
            throw new FlashException(failure);

        progress.Report(new FlashProgress(100, Loc.Get("Flash_Progress_Rebooting")));
    }
}
```

- [ ] **Step 5: Add the `Esp32` flasher property to `FirmwareCatalog`**

Add to `Core/Services/Firmware/FirmwareCatalog.cs`:

```csharp
    /// <summary>The ESP32 flasher, or null if the bundled assets are missing/mismatched.</summary>
    public IBoardFlasher? Esp32
    {
        get
        {
            var manifest = Esp32Manifest;
            if (manifest is null) return null;
            var bin = Esp32BinPath(manifest);
            if (!File.Exists(EsptoolPath) || !manifest.VerifyBin(bin)) return null;
            return new Esp32Flasher(manifest, EsptoolPath, bin);
        }
    }
```

- [ ] **Step 6: Add strings to both `.resw` files**

Add these `<data>` entries to `Strings/en-US/Resources.resw`:

```xml
  <data name="Flash_Board_Esp32" xml:space="preserve"><value>ESP32 (round display)</value></data>
  <data name="Flash_Progress_Detecting" xml:space="preserve"><value>Connecting to the board…</value></data>
  <data name="Flash_Progress_Writing" xml:space="preserve"><value>Writing… {0}%</value></data>
  <data name="Flash_Progress_Rebooting" xml:space="preserve"><value>Flash complete, rebooting…</value></data>
  <data name="Flash_Err_NoPort" xml:space="preserve"><value>No COM port is selected. Choose your controller's port in Serial settings first.</value></data>
  <data name="Flash_Err_EsptoolMissing" xml:space="preserve"><value>The flashing tool is missing from this install. Reinstall Dialed and try again.</value></data>
  <data name="Flash_Err_BinMissing" xml:space="preserve"><value>The bundled firmware image is missing from this install. Reinstall Dialed and try again.</value></data>
  <data name="Flash_Err_WrongChip" xml:space="preserve"><value>This board isn't a classic ESP32 (WROOM). Other chips (S3/C3/…) aren't supported yet.</value></data>
  <data name="Flash_Err_NotBootloader" xml:space="preserve"><value>Couldn't reach the ESP32 bootloader. Hold the BOOT button on the board, then click Flash again.</value></data>
  <data name="Flash_Err_PortBusy" xml:space="preserve"><value>The COM port is in use by another program. Close it (including any serial monitor) and try again.</value></data>
  <data name="Flash_Err_WriteFailed" xml:space="preserve"><value>Flashing failed: {0}</value></data>
  <data name="Flash_Err_Cancelled" xml:space="preserve"><value>Flashing was cancelled.</value></data>
```

Add the same keys to `Strings/pt-PT/Resources.resw` with Portuguese values:

```xml
  <data name="Flash_Board_Esp32" xml:space="preserve"><value>ESP32 (ecrã redondo)</value></data>
  <data name="Flash_Progress_Detecting" xml:space="preserve"><value>A ligar à placa…</value></data>
  <data name="Flash_Progress_Writing" xml:space="preserve"><value>A gravar… {0}%</value></data>
  <data name="Flash_Progress_Rebooting" xml:space="preserve"><value>Gravação concluída, a reiniciar…</value></data>
  <data name="Flash_Err_NoPort" xml:space="preserve"><value>Nenhuma porta COM selecionada. Escolha a porta do controlador nas definições de série primeiro.</value></data>
  <data name="Flash_Err_EsptoolMissing" xml:space="preserve"><value>A ferramenta de gravação não está nesta instalação. Reinstale o Dialed e tente novamente.</value></data>
  <data name="Flash_Err_BinMissing" xml:space="preserve"><value>A imagem de firmware incluída não está nesta instalação. Reinstale o Dialed e tente novamente.</value></data>
  <data name="Flash_Err_WrongChip" xml:space="preserve"><value>Esta placa não é um ESP32 clássico (WROOM). Outros chips (S3/C3/…) ainda não são suportados.</value></data>
  <data name="Flash_Err_NotBootloader" xml:space="preserve"><value>Não foi possível aceder ao bootloader do ESP32. Mantenha o botão BOOT premido e clique em Gravar novamente.</value></data>
  <data name="Flash_Err_PortBusy" xml:space="preserve"><value>A porta COM está a ser usada por outro programa. Feche-o (incluindo qualquer monitor série) e tente novamente.</value></data>
  <data name="Flash_Err_WriteFailed" xml:space="preserve"><value>A gravação falhou: {0}</value></data>
  <data name="Flash_Err_Cancelled" xml:space="preserve"><value>A gravação foi cancelada.</value></data>
```

- [ ] **Step 7: Verify build + parsing helpers**

Run: `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`
Expected: build succeeds.
Confirm by inspection/scratch:
- `Esp32Flasher.ParsePercent("Writing at 0x00010000... (37 %)") == 37`; `ParsePercent("Connecting....") == -1`.
- `Esp32Flasher.ClassifyFailure(0, "") == null`; `ClassifyFailure(2, "A fatal error occurred: Failed to connect to ESP32: Wrong boot mode detected") == Loc.Get("Flash_Err_NotBootloader")`.

- [ ] **Step 8: Verify on hardware (manual, end-to-end)**

With a real WROOM connected on its COM port and the app **not** holding the port (stop the app, or use a scratch console calling `FlashAsync`), run the flash.
Expected: progress climbs 0→100, board reboots, and (from Task 1) it emits `fw:esp32-mixer:1.0.0`. Test the BOOT-hint path by starting a flash on a bare board without holding BOOT and confirming the `Flash_Err_NotBootloader` message.

- [ ] **Step 9: Commit**

```bash
git add Core/Services/Firmware Strings/en-US/Resources.resw Strings/pt-PT/Resources.resw
git commit -m "feat: esptool-backed ESP32 flasher with progress + localized errors"
```

---

## Task 6: MainViewModel — installed version + flash bracket

**Files:**
- Modify: `Core/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `SerialManager.FirmwareReported`/`RequestFirmwareVersion` (Task 4); `FirmwareCatalog`/`IBoardFlasher`/`FlashProgress`/`FlashException` (Tasks 3, 5).
- Produces:
  - `string? InstalledFirmwareVersion` (`[ObservableProperty]`).
  - `IBoardFlasher? Esp32Flasher` (from the catalog; null if assets missing).
  - `Task FlashControllerAsync(IProgress<FlashProgress> progress, CancellationToken ct)` — brackets the flash (suspend watchdog, release port, flash, reconnect).

- [ ] **Step 1: Add the catalog field, observable property, and flasher accessor**

Add `using Dialed.Core.Services.Firmware;` to the usings. Add fields/properties near the other members:

```csharp
    private readonly FirmwareCatalog _firmwareCatalog = new(FirmwareCatalog.DefaultBaseDir);

    /// <summary>Firmware version the connected controller reports, or null if unknown.</summary>
    [ObservableProperty]
    private string? installedFirmwareVersion;

    /// <summary>The ESP32 flasher (bundled firmware), or null if assets are missing.</summary>
    public IBoardFlasher? Esp32Flasher => _firmwareCatalog.Esp32;

    /// <summary>Bundled firmware version available to flash, or null if unavailable.</summary>
    public string? BundledFirmwareVersion => _firmwareCatalog.Esp32?.FirmwareVersion;
```

- [ ] **Step 2: Wire the `FirmwareReported` handler in subscribe/unsubscribe**

In `CreateAndStartSerial`, add the subscription alongside the others:

```csharp
        serial.FirmwareReported += OnFirmwareReported;
```

In `UnsubscribeSerial`, add the matching removal:

```csharp
        serial.FirmwareReported -= OnFirmwareReported;
```

Add the handler (marshals to the UI thread, since the event fires on the serial thread):

```csharp
    private void OnFirmwareReported(string board, string version)
        => _dispatcherQueue.TryEnqueue(() => InstalledFirmwareVersion = version);
```

- [ ] **Step 3: Ask for the version after (re)connect**

In `ScheduleResync`, request the version once the board has booted. Change the body to:

```csharp
    private void ScheduleResync() => _ = Task.Run(async () =>
    {
        await Task.Delay(2000);
        _dispatcherQueue.TryEnqueue(() =>
        {
            SyncAllChannels();
            _serial.RequestFirmwareVersion();
        });
    });
```

Also clear the stale value when a drop is detected — in `CheckConnection`, in the branch that sets `SerialStatus = Loc.Get("Serial_DeviceRemoved")` (both places), add:

```csharp
                InstalledFirmwareVersion = null;
```

- [ ] **Step 4: Add `FlashControllerAsync` (the serial bracket)**

```csharp
    /// <summary>
    /// Flashes the ESP32 controller with the bundled firmware. Suspends the
    /// auto-reconnect watchdog and fully releases the COM port for the duration
    /// (esptool needs exclusive access), then reconnects. Throws FlashException
    /// with a localized reason on failure.
    /// </summary>
    public async Task FlashControllerAsync(IProgress<FlashProgress> progress, CancellationToken ct)
    {
        var flasher = Esp32Flasher
            ?? throw new FlashException(Loc.Get("Flash_Err_EsptoolMissing"));

        var port = ComPort;
        var watchdogWasRunning = _connectionTimer.IsEnabled;

        // 1. Suspend the watchdog so it can't re-grab the port mid-flash.
        _connectionTimer.Stop();
        // 2. Fully release the port.
        DetachSerial();
        InstalledFirmwareVersion = null;

        try
        {
            // 3. Flash (esptool owns the port here).
            await flasher.FlashAsync(port, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            // 4. Re-open and re-arm, regardless of success/failure.
            _serial = CreateAndStartSerial();
            ScheduleResync();
            if (watchdogWasRunning)
                _connectionTimer.Start();
        }
    }
```

- [ ] **Step 5: Verify build**

Run: `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`
Expected: build succeeds.

- [ ] **Step 6: Verify handshake end-to-end (manual)**

Run the app with a controller already flashed with Task 1 firmware. Enable **Diagnostics → Debug serial events** if needed. Connect the board.
Expected: within ~2s of connect, `InstalledFirmwareVersion` becomes `1.0.0` (observe in the debugger, or via the UI once Task 8 binds it). Unplug the board → it resets to `null`.

- [ ] **Step 7: Commit**

```bash
git add Core/ViewModels/MainViewModel.cs
git commit -m "feat: installed-firmware readout + FlashControllerAsync serial bracket"
```

---

## Task 7: Flash dialog (view + view model)

**Files:**
- Create: `Core/ViewModels/FlashFirmwareViewModel.cs`
- Create: `Core/Controls/FlashFirmwareDialog.xaml`
- Create: `Core/Controls/FlashFirmwareDialog.xaml.cs`
- Modify: `Strings/en-US/Resources.resw`, `Strings/pt-PT/Resources.resw` (dialog strings)

**Interfaces:**
- Consumes: `MainViewModel.FlashControllerAsync`/`BundledFirmwareVersion`/`InstalledFirmwareVersion`/`ComPort`; `FlashProgress` (Task 6/5).
- Produces: `FlashFirmwareViewModel` with `StartFlashAsync()`, observable `IsFlashing`/`Percent`/`StatusText`/`ResultMessage`/`IsError`/`CanFlash`; `FlashFirmwareDialog : ContentDialog` shown from Settings (Task 8).

- [ ] **Step 1: Create `FlashFirmwareViewModel.cs`**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Dialed.Core.Services;
using Dialed.Core.Services.Firmware;
using Dialed.Core.ViewModels;

namespace Dialed.Core.ViewModels;

public partial class FlashFirmwareViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public FlashFirmwareViewModel(MainViewModel main)
    {
        _main = main;
    }

    public string BoardName => _main.Esp32Flasher?.DisplayName ?? Loc.Get("Flash_Board_Esp32");
    public string BundledVersionText => Loc.Get("Flash_Dialog_BundledVersion", _main.BundledFirmwareVersion ?? "—");
    public string PortText => Loc.Get("Flash_Dialog_Port", string.IsNullOrWhiteSpace(_main.ComPort) ? "—" : _main.ComPort);

    /// <summary>True when a port is selected and firmware assets exist.</summary>
    public bool CanFlash => !IsFlashing && _main.Esp32Flasher is not null && !string.IsNullOrWhiteSpace(_main.ComPort);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFlash))]
    private bool isFlashing;

    [ObservableProperty]
    private int percent;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private string resultMessage = "";

    [ObservableProperty]
    private bool isError;

    public async Task StartFlashAsync()
    {
        if (!CanFlash) return;

        HasResult = false;
        IsError = false;
        Percent = 0;
        StatusText = "";
        IsFlashing = true;
        _cts = new CancellationTokenSource();

        var progress = new Progress<FlashProgress>(p =>
        {
            Percent = p.Percent;
            StatusText = p.StatusText;
        });

        try
        {
            await _main.FlashControllerAsync(progress, _cts.Token);
            IsError = false;
            ResultMessage = Loc.Get("Flash_Success");
        }
        catch (FlashException ex)
        {
            IsError = true;
            ResultMessage = ex.Message;
        }
        catch (Exception ex)
        {
            IsError = true;
            ResultMessage = Loc.Get("Flash_Err_WriteFailed", ex.Message);
        }
        finally
        {
            IsFlashing = false;
            HasResult = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
```

- [ ] **Step 2: Create `FlashFirmwareDialog.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<ContentDialog
    x:Class="Dialed.Core.Controls.FlashFirmwareDialog"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:loc="using:Dialed.Core.Services"
    Title="{loc:Loc Key=Flash_Dialog_Title}"
    PrimaryButtonText="{loc:Loc Key=Flash_Dialog_FlashNow}"
    CloseButtonText="{loc:Loc Key=Common_Close}"
    DefaultButton="Primary">

    <StackPanel Spacing="12" MinWidth="360">
        <TextBlock Text="{x:Bind ViewModel.BoardName, Mode=OneWay}" FontWeight="SemiBold" />
        <TextBlock Text="{x:Bind ViewModel.BundledVersionText, Mode=OneWay}" Opacity="0.8" />
        <TextBlock Text="{x:Bind ViewModel.PortText, Mode=OneWay}" Opacity="0.8" />

        <StackPanel Spacing="6" Visibility="{x:Bind ViewModel.IsFlashing, Mode=OneWay}">
            <ProgressBar Value="{x:Bind ViewModel.Percent, Mode=OneWay}" Maximum="100" />
            <TextBlock Text="{x:Bind ViewModel.StatusText, Mode=OneWay}" Opacity="0.8" />
        </StackPanel>

        <InfoBar
            IsOpen="{x:Bind ViewModel.HasResult, Mode=OneWay}"
            IsClosable="False"
            Message="{x:Bind ViewModel.ResultMessage, Mode=OneWay}"
            Severity="Informational" />

        <TextBlock
            Text="{loc:Loc Key=Flash_BootHint}"
            TextWrapping="Wrap" FontSize="12" Opacity="0.7" />
    </StackPanel>
</ContentDialog>
```

- [ ] **Step 3: Create `FlashFirmwareDialog.xaml.cs`**

```csharp
using Dialed.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Dialed.Core.Controls;

public sealed partial class FlashFirmwareDialog : ContentDialog
{
    public FlashFirmwareViewModel ViewModel { get; }

    public FlashFirmwareDialog(MainViewModel main)
    {
        ViewModel = new FlashFirmwareViewModel(main);
        InitializeComponent();

        // Keep the dialog open across the flash: intercept Primary, run the flash,
        // and only surface the result. Close is disabled while flashing.
        PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                // Cancel closes the dialog; while flashing, block the close button.
                IsPrimaryButtonEnabled = false;
                await ViewModel.StartFlashAsync();
                // Re-purpose the primary button state after completion.
                IsPrimaryButtonEnabled = ViewModel.CanFlash;
            }
            finally
            {
                // Keep the dialog open so the user sees the result InfoBar; they
                // dismiss with Close. Prevent auto-close on this click.
                args.Cancel = true;
                deferral.Complete();
            }
        };
    }
}
```

- [ ] **Step 4: Add dialog strings to both `.resw` files**

`Strings/en-US/Resources.resw`:

```xml
  <data name="Flash_Dialog_Title" xml:space="preserve"><value>Flash controller firmware</value></data>
  <data name="Flash_Dialog_FlashNow" xml:space="preserve"><value>Flash</value></data>
  <data name="Flash_Dialog_BundledVersion" xml:space="preserve"><value>Firmware to install: {0}</value></data>
  <data name="Flash_Dialog_Port" xml:space="preserve"><value>Port: {0}</value></data>
  <data name="Flash_Success" xml:space="preserve"><value>Firmware flashed successfully. Reconnecting…</value></data>
  <data name="Flash_BootHint" xml:space="preserve"><value>If flashing doesn't start, hold the BOOT button on the board while clicking Flash.</value></data>
```

`Strings/pt-PT/Resources.resw`:

```xml
  <data name="Flash_Dialog_Title" xml:space="preserve"><value>Gravar firmware do controlador</value></data>
  <data name="Flash_Dialog_FlashNow" xml:space="preserve"><value>Gravar</value></data>
  <data name="Flash_Dialog_BundledVersion" xml:space="preserve"><value>Firmware a instalar: {0}</value></data>
  <data name="Flash_Dialog_Port" xml:space="preserve"><value>Porta: {0}</value></data>
  <data name="Flash_Success" xml:space="preserve"><value>Firmware gravado com sucesso. A reconectar…</value></data>
  <data name="Flash_BootHint" xml:space="preserve"><value>Se a gravação não iniciar, mantenha o botão BOOT premido enquanto clica em Gravar.</value></data>
```

> `Common_Close` is assumed to exist (used elsewhere). If a build error reports it missing, add `<data name="Common_Close"><value>Close</value></data>` (en) / `<value>Fechar</value>` (pt).

- [ ] **Step 5: Verify build**

Run: `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`
Expected: build succeeds (XAML compiles, no missing-resource errors).

- [ ] **Step 6: Commit**

```bash
git add Core/ViewModels/FlashFirmwareViewModel.cs Core/Controls/FlashFirmwareDialog.xaml Core/Controls/FlashFirmwareDialog.xaml.cs Strings/en-US/Resources.resw Strings/pt-PT/Resources.resw
git commit -m "feat: flash firmware dialog with live progress + result"
```

---

## Task 8: Settings "Firmware" section

**Files:**
- Modify: `Core/Views/SettingsPage.xaml` (new Firmware section)
- Modify: `Core/Views/SettingsPage.xaml.cs` (button handler opens the dialog)
- Modify: `Strings/en-US/Resources.resw`, `Strings/pt-PT/Resources.resw` (section strings)

**Interfaces:**
- Consumes: `MainViewModel.InstalledFirmwareVersion`/`BundledFirmwareVersion` (Task 6); `FlashFirmwareDialog` (Task 7).

- [ ] **Step 1: Add a converter-free installed-version readout property to `MainViewModel`**

So the XAML can bind a single friendly string, add to `MainViewModel` (near `BundledFirmwareVersion`), and raise it when `InstalledFirmwareVersion` changes:

```csharp
    public string FirmwareInstalledText => string.IsNullOrEmpty(InstalledFirmwareVersion)
        ? Loc.Get("Settings_Firmware_Installed_Unknown")
        : Loc.Get("Settings_Firmware_Installed", InstalledFirmwareVersion);

    partial void OnInstalledFirmwareVersionChanged(string? value)
        => OnPropertyChanged(nameof(FirmwareInstalledText));
```

- [ ] **Step 2: Add the Firmware section to `SettingsPage.xaml`**

Insert this block after the Controller section's closing `</Border>` (before the Audio Sessions section):

```xml
            <!-- ==================== Firmware ==================== -->
            <TextBlock Text="{loc:Loc Key=Settings_Firmware_Title}" Style="{StaticResource SectionTitle}" Margin="0,20,0,0" />
            <TextBlock Text="{loc:Loc Key=Settings_Firmware_Caption}"
                       Style="{StaticResource SectionCaption}" />

            <Border Style="{StaticResource Card}" Margin="0,6,0,0">
                <StackPanel Spacing="14">
                    <StackPanel Spacing="6">
                        <TextBlock Text="{x:Bind ViewModel.FirmwareInstalledText, Mode=OneWay}"
                                   Style="{StaticResource FieldLabel}" />
                        <TextBlock Text="{loc:Loc Key=Settings_Firmware_Desc}"
                                   Style="{StaticResource FieldHelp}" />
                    </StackPanel>

                    <Button Content="{loc:Loc Key=Settings_Firmware_FlashButton}"
                            Click="OnFlashFirmwareClick"
                            BorderThickness="1" CornerRadius="8"
                            HorizontalAlignment="Left" />
                </StackPanel>
            </Border>
```

- [ ] **Step 3: Add the click handler to `SettingsPage.xaml.cs`**

Add `using Dialed.Core.Controls;` to the usings, and the handler:

```csharp
    private async void OnFlashFirmwareClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FlashFirmwareDialog(ViewModel) { XamlRoot = this.XamlRoot };
        await dialog.ShowAsync();
    }
```

- [ ] **Step 4: Add section strings to both `.resw` files**

`Strings/en-US/Resources.resw`:

```xml
  <data name="Settings_Firmware_Title" xml:space="preserve"><value>Firmware</value></data>
  <data name="Settings_Firmware_Caption" xml:space="preserve"><value>Install the controller firmware directly from Dialed — no Arduino tools needed.</value></data>
  <data name="Settings_Firmware_Desc" xml:space="preserve"><value>Flashes the bundled firmware to a connected classic ESP32 (WROOM) over the selected COM port.</value></data>
  <data name="Settings_Firmware_FlashButton" xml:space="preserve"><value>Flash controller firmware</value></data>
  <data name="Settings_Firmware_Installed" xml:space="preserve"><value>Installed firmware: v{0}</value></data>
  <data name="Settings_Firmware_Installed_Unknown" xml:space="preserve"><value>Installed firmware: unknown (connect the controller)</value></data>
```

`Strings/pt-PT/Resources.resw`:

```xml
  <data name="Settings_Firmware_Title" xml:space="preserve"><value>Firmware</value></data>
  <data name="Settings_Firmware_Caption" xml:space="preserve"><value>Instale o firmware do controlador diretamente no Dialed — sem ferramentas Arduino.</value></data>
  <data name="Settings_Firmware_Desc" xml:space="preserve"><value>Grava o firmware incluído num ESP32 clássico (WROOM) ligado, através da porta COM selecionada.</value></data>
  <data name="Settings_Firmware_FlashButton" xml:space="preserve"><value>Gravar firmware do controlador</value></data>
  <data name="Settings_Firmware_Installed" xml:space="preserve"><value>Firmware instalado: v{0}</value></data>
  <data name="Settings_Firmware_Installed_Unknown" xml:space="preserve"><value>Firmware instalado: desconhecido (ligue o controlador)</value></data>
```

- [ ] **Step 5: Verify build + run**

Run: `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`
Expected: build succeeds.
Run the app (`Dialed (Unpackaged)` profile). Open Settings → Firmware.
Expected: the section shows "Installed firmware: unknown" when disconnected (or `v1.0.0` when a Task-1 board is connected), and the **Flash controller firmware** button opens the dialog showing board, bundled version `1.0.0`, and the current port.

- [ ] **Step 6: Full end-to-end (manual, real board)**

With a real WROOM connected: open the dialog, click **Flash**, watch the progress bar climb to 100%, see the success InfoBar, and confirm the Firmware section then shows `Installed firmware: v1.0.0` after auto-reconnect. Confirm the mixer still responds to knobs (serial re-synced).

- [ ] **Step 7: Commit**

```bash
git add Core/Views/SettingsPage.xaml Core/Views/SettingsPage.xaml.cs Core/ViewModels/MainViewModel.cs Strings/en-US/Resources.resw Strings/pt-PT/Resources.resw
git commit -m "feat: Settings Firmware section with flash button + installed-version readout"
```

---

## Self-Review

**Spec coverage:**
- §1 Firmware versioning (`version.h`, single source) → Task 1. ✓
- §2 Build pipeline (`build-firmware.ps1`, merged bin, `manifest.json`) → Task 2. ✓
- §3 Bundled assets (esptool, bin, manifest via `Content CopyToOutputDirectory`) + installer (recursesubdirs, verify-only) → Task 2 (steps 4–5). ✓
- §4 Flasher abstraction (`IBoardFlasher`, `Esp32Flasher`, `FirmwareCatalog`, `FlashProgress`, `FlashException`) → Tasks 3, 5. ✓
- §5 Serial-port coordination (`FlashControllerAsync`: suspend watchdog, release port, reconnect) → Task 6. ✓
- §6 Version handshake (firmware `fw:`/`ver?`; `SerialManager` event/request; `MainViewModel` wiring + `ver?` on connect) → Tasks 1, 4, 6. ✓
- §7 UI (Firmware section, dialog with live progress, installed vs bundled, BOOT hint, success/error) → Tasks 7, 8. ✓
- §8 Localization (all strings in both resw) → Tasks 5, 7, 8. ✓
- Error handling reasons (no port, esptool missing, wrong chip, not-bootloader, port busy, write failed, cancelled, bin missing) → Task 5. ✓
- Testing (manual, per spec) → each task's verification step + Task 8 end-to-end. ✓ (deviation documented up front)

**Placeholder scan:** No TBD/TODO; every code step shows complete code. The only conditional is the `Common_Close` fallback note (Task 7 step 4), which gives exact remediation. ✓

**Type consistency:** `FlashProgress(int Percent, string StatusText)` used identically in Tasks 5–7; `IBoardFlasher.FlashAsync(string, IProgress<FlashProgress>, CancellationToken)` matches `Esp32Flasher` and `FlashControllerAsync`'s call; `FirmwareCatalog.Esp32` (`IBoardFlasher?`) consumed as `MainViewModel.Esp32Flasher`; `FirmwareReported(string board, string version)` consistent across `SerialManager`/`MainViewModel`; `InstalledFirmwareVersion`/`BundledFirmwareVersion` names stable across Tasks 6–8. ✓

**Update-while-running interaction (extra check):** `FlashControllerAsync` stops `_connectionTimer` and restores it only if it was running — consistent with the existing `AutoReconnect` gating; no conflict with the `WM_QUERYENDSESSION` upgrade path (that exits the process, which would also cancel a flash via `ct`). ✓
