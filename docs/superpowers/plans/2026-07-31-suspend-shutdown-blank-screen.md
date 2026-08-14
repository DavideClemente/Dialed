# Blank Controller Screen on PC Suspend/Shutdown Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When Windows suspends or shuts down, the ESP32 controller (`Arduino/mixer/`) blanks its GC9A01
display and puts the panel to sleep instead of falling into its normal animated idle screen; it wakes
immediately on PC resume or reconnect.

**Architecture:** A new symmetric serial command pair, `screen:off` / `screen:on`, gates a `blankMode`
flag in the firmware's display module (mirroring the existing `uploadMode` gate that already "owns" the
display during a GIF upload). The app sends these from two triggers: `Microsoft.Win32.SystemEvents
.PowerModeChanged` (suspend/resume) and the existing `WM_ENDSESSION` window-message handler (shutdown/
logoff).

**Tech Stack:** Arduino C++ (`TFT_eSPI`, ESP32 core) for the firmware; C# / WinUI 3 (.NET 8) for the app.
No test framework exists on either side — verification is compile checks (`arduino-cli compile`, `dotnet
build`) plus a manual hardware pass at the end.

## Global Constraints

- Firmware scope is `Arduino/mixer/` (ESP32/GC9A01) only — do not touch `mixer_nano/` or `arduino/`.
- Bump `Arduino/mixer/version.h`'s `FW_VERSION` (SemVer) in the same commit as any `mixer/` change, per
  `Arduino/README.md` and `CLAUDE.md`. This is a new backward-compatible feature → bump the **minor**
  version: `1.1.0` → `1.2.0`.
- App builds always need `-p:Platform=x64` (or `x86`/`ARM64`) — there is no AnyCPU:
  `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`.
- If Dialed is currently running from the tray while you build, the build reports 0 errors but the exe
  never gets replaced (the running process holds the file lock) — quit it from the tray first.
- Manual "Quit" from the tray dialog must NOT trigger the blank screen — only real suspend/shutdown does.
- No new backlight/GPIO wiring — see the design spec's rejected alternatives.

---

## Task 1: Firmware — blank-mode core in the display module

**Files:**
- Modify: `Arduino/mixer/display.h`
- Modify: `Arduino/mixer/display.cpp`

**Interfaces:**
- Produces: `void displayBlank(bool blank)` — public API. `true` blanks + sleeps the panel; `false` wakes
  it and marks the current mode dirty so the next `displayTick()` redraws cleanly. Consumed by Task 2.

- [ ] **Step 1: Add the `displayBlank` declaration to the header**

In `Arduino/mixer/display.h`, add the new declaration next to the other mode-transition functions:

```c
#pragma once

void displaySetup();
void displayShowKnob(int knobIndex, float value);
void displayShowMute(int knobIndex, bool muted);
void displaySetShowPercent(bool show);
void displayEnterIdle();
void displayBlank(bool blank);
void displayTick();
```

(Only the `void displayBlank(bool blank);` line is new — inserted after `displayEnterIdle();`.)

- [ ] **Step 2: Add the `blankMode` state flag**

In `Arduino/mixer/display.cpp`, find this block (around line 58):

```cpp
static bool  gifMode    = false;   // idle screen is playing a stored GIF
static bool  uploadMode = false;   // showing the GIF-upload progress screen
```

Add `blankMode` right after `uploadMode`:

```cpp
static bool  gifMode    = false;   // idle screen is playing a stored GIF
static bool  uploadMode = false;   // showing the GIF-upload progress screen
static bool  blankMode  = false;   // screen frozen for PC suspend/shutdown
```

- [ ] **Step 3: Implement `displayBlank()`**

In `Arduino/mixer/display.cpp`, find `displayEnterIdle()`:

```cpp
// Call when all knobs are idle. Switches to IDLE animated screen.
void displayEnterIdle() {
  if (mode == IDLE) return;
  mode      = IDLE;
  idleDirty = true;
}
```

Add `displayBlank()` directly after it (before the `// ── GIF-upload progress screen` comment block):

```cpp
// Freeze/unfreeze the display for PC suspend/shutdown (blank=true) and PC
// resume/reconnect (blank=false). Blanking also sends the GC9A01 into its own
// sleep state (DISPOFF+SLPIN) — real power savings on the panel's driving
// circuitry, though the backlight itself stays lit (it's hardwired to 3.3V,
// not GPIO-controlled — see Arduino/PINOUT.md). Waking reverses the sequence
// (SLPOUT, a mandatory settle delay, then DISPON) before the normal redraw
// resumes on the next displayTick().
void displayBlank(bool blank) {
  if (blank == blankMode) return;

  if (blank) {
    tft.fillScreen(TFT_BLACK);
    tft.writecommand(0x28);  // DISPOFF
    tft.writecommand(0x10);  // SLPIN
    blankMode = true;
  } else {
    tft.writecommand(0x11);  // SLPOUT
    delay(120);               // panel-mandated wake settle time before further commands
    tft.writecommand(0x29);  // DISPON
    blankMode = false;
    idleDirty = true;
    appDirty  = true;
  }
}
```

- [ ] **Step 4: Gate `displayTick()` on `blankMode`**

In `Arduino/mixer/display.cpp`, find:

```cpp
// Call every loop(). Advances animation state and redraws only changed regions.
void displayTick() {
  if (uploadMode) return;   // upload screen owns the display until it finishes
  unsigned long now = millis();
```

Change to:

```cpp
// Call every loop(). Advances animation state and redraws only changed regions.
void displayTick() {
  if (blankMode) return;    // screen frozen for PC suspend/shutdown — see displayBlank()
  if (uploadMode) return;   // upload screen owns the display until it finishes
  unsigned long now = millis();
```

- [ ] **Step 5: Gate `displayShowKnob()` and `displayShowMute()` on `blankMode`**

These are called both from PC `vol:`/`mute:` lines *and* directly from local knob hardware events
(`onKnobChange` in `mixer.ino`), so without this guard a physical knob turn during a PC-asleep blank
would silently re-arm the active screen the instant blank mode ends.

In `Arduino/mixer/display.cpp`, find:

```cpp
void displayShowMute(int knobIndex, bool muted) {
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
```

Change to:

```cpp
void displayShowMute(int knobIndex, bool muted) {
  if (blankMode) return;
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
```

Find:

```cpp
void displayShowKnob(int knobIndex, float value) {
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
```

Change to:

```cpp
void displayShowKnob(int knobIndex, float value) {
  if (blankMode) return;
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
```

- [ ] **Step 6: Compile-check**

Run from the repo root:

```bash
arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer
```

Expected: builds cleanly (same "Sketch uses ... / Global variables use ..." success output as before
this change — `displayBlank` isn't called from anywhere yet, so there's nothing to exercise at runtime,
just confirm it compiles and links).

- [ ] **Step 7: Commit**

```bash
git add Arduino/mixer/display.h Arduino/mixer/display.cpp
git commit -m "firmware: add displayBlank() to freeze/sleep the GC9A01 panel"
```

---

## Task 2: Firmware — `screen:off`/`screen:on` protocol wiring, version bump, README

**Files:**
- Modify: `Arduino/mixer/mixer.ino`
- Modify: `Arduino/mixer/version.h`
- Modify: `Arduino/README.md`

**Interfaces:**
- Consumes: `void displayBlank(bool blank)` from Task 1.
- Produces: the `screen:off` / `screen:on` wire protocol, documented and parsed — consumed by Task 3
  (`SerialManager` sends these lines).

- [ ] **Step 1: Add the line handler**

In `Arduino/mixer/mixer.ino`, find `handleConfigLine()`:

```cpp
static void handleConfigLine(const char* line) {
  if (strncmp(line, "cfg:idle:", 9) == 0) {
    long ms = atol(line + 9);
    if (ms >= 0) idleTimeoutMs = (unsigned long)ms;
  } else if (strncmp(line, "config:pct:", 11) == 0) {
    displaySetShowPercent(atoi(line + 11) != 0);
  } else if (strncmp(line, "cfg:knobs:", 10) == 0) {
    knobsSetActiveCount(atoi(line + 10));
  }
}
```

Add a new handler directly after it:

```cpp
static void handleScreenLine(const char* line) {
  if (strcmp(line, "screen:off") == 0) {
    displayBlank(true);
  } else if (strcmp(line, "screen:on") == 0) {
    displayBlank(false);
  }
}
```

- [ ] **Step 2: Wire it into `readIncomingSerial()`**

In `Arduino/mixer/mixer.ino`, find:

```cpp
        if (!idleGifHandleLine(inLine)) {
          handleAssignLine(inLine);
          handleIconLine(inLine);
          handleVolumeLine(inLine);
          handleMuteLine(inLine);
          handleConfigLine(inLine);
          handleVerLine(inLine);
        }
```

Add `handleScreenLine(inLine);` after `handleConfigLine(inLine);`:

```cpp
        if (!idleGifHandleLine(inLine)) {
          handleAssignLine(inLine);
          handleIconLine(inLine);
          handleVolumeLine(inLine);
          handleMuteLine(inLine);
          handleConfigLine(inLine);
          handleScreenLine(inLine);
          handleVerLine(inLine);
        }
```

- [ ] **Step 3: Bump the firmware version**

In `Arduino/mixer/version.h`, change:

```c
#define FW_VERSION "1.1.0"
```

to:

```c
#define FW_VERSION "1.2.0"
```

- [ ] **Step 4: Document the protocol addition**

In `Arduino/README.md`, find the "PC → Board" protocol list:

```
**PC → Board** (display tiers; knobs-only/Nano ignore what they can't use)
- `vol:knob1:0.42` — authoritative volume echo; drives the on-device gauge
- `assign:knob1:RRGGBB:AppName` — label + accent color for a knob
- `icon:knob1:<base64>` — 64×64 RGB565 icon (~11 KB). **ESP32 only**; the Nano build discards
  these lines without buffering them.
```

Add a new bullet after the `icon:` line:

```
**PC → Board** (display tiers; knobs-only/Nano ignore what they can't use)
- `vol:knob1:0.42` — authoritative volume echo; drives the on-device gauge
- `assign:knob1:RRGGBB:AppName` — label + accent color for a knob
- `icon:knob1:<base64>` — 64×64 RGB565 icon (~11 KB). **ESP32 only**; the Nano build discards
  these lines without buffering them.
- `screen:off` / `screen:on` — blank + sleep / wake the display around PC suspend, sleep-resume,
  and shutdown. **ESP32 only** (`mixer/`); other tiers ignore it.
```

- [ ] **Step 5: Compile-check**

```bash
arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer
```

Expected: builds cleanly.

- [ ] **Step 6: Commit**

```bash
git add Arduino/mixer/mixer.ino Arduino/mixer/version.h Arduino/README.md
git commit -m "firmware: parse screen:off/screen:on, bump FW_VERSION to 1.2.0"
```

---

## Task 3: App — `SerialManager` screen-blank command methods

**Files:**
- Modify: `Core/SerialManager.cs`

**Interfaces:**
- Produces: `public void SendScreenOff()`, `public void SendScreenOn()` — consumed by Tasks 4 and 5.

- [ ] **Step 1: Add the two methods**

In `Core/SerialManager.cs`, find `RequestFirmwareVersion()`:

```csharp
    /// <summary>Asks the controller to (re)report its firmware version via a "fw:" line.</summary>
    public void RequestFirmwareVersion()
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine("ver?"); }
        catch { }
    }
```

Add the new methods directly after it, before `SendAssignment`:

```csharp
    /// <summary>Blanks and sleeps the controller's display (PC suspend/shutdown). Parsed by
    /// handleScreenLine on the device.</summary>
    public void SendScreenOff()
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine("screen:off"); }
        catch { }
    }

    /// <summary>Wakes and restores the controller's display (PC resume/reconnect). Parsed by
    /// handleScreenLine on the device.</summary>
    public void SendScreenOn()
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine("screen:on"); }
        catch { }
    }
```

- [ ] **Step 2: Build**

Quit Dialed from the tray if it's currently running (see Global Constraints), then:

```bash
dotnet build Dialed.csproj -p:Platform=x64 -c Debug
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add Core/SerialManager.cs
git commit -m "app: add SerialManager.SendScreenOff/SendScreenOn"
```

---

## Task 4: App — auto-blank on PC suspend/resume

**Files:**
- Modify: `Core/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `SerialManager.SendScreenOff()` / `SendScreenOn()` from Task 3; `_dispatcherQueue`
  (`DispatcherQueue`, already a field, `MainViewModel.cs:24`); `_serial` (`SerialManager`, already a
  field, `MainViewModel.cs:31`).
- Produces: `public void SendScreenOff()` on `MainViewModel` — consumed by Task 5 (`MainWindow`).

- [ ] **Step 1: Add the `Microsoft.Win32` using**

In `Core/ViewModels/MainViewModel.cs`, find the using block at the top:

```csharp
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage;
```

Add `using Microsoft.Win32;` above it, keeping alphabetical order with the existing `Microsoft.*` usings:

```csharp
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Windows.Storage;
```

- [ ] **Step 2: Subscribe in the constructor**

In `Core/ViewModels/MainViewModel.cs`, find the end of the constructor:

```csharp
        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionTimer.Tick += (_, _) => CheckConnection();
        if (AutoReconnect)
            _connectionTimer.Start();
    }
```

Add the subscription right before the closing brace:

```csharp
        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionTimer.Tick += (_, _) => CheckConnection();
        if (AutoReconnect)
            _connectionTimer.Start();

        // Never unsubscribed — MainViewModel is a process-lifetime singleton, same as
        // the timers above and the tray icon (MainWindow), none of which are disposed
        // until the process exits.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }
```

- [ ] **Step 3: Add the handler and the shutdown passthrough**

In `Core/ViewModels/MainViewModel.cs`, find `ScheduleResync()`:

```csharp
    // Waits for the controller to finish booting after a (re)connect, then pushes
    // assignments/config. Shared by first launch, manual reconnect, and auto-reconnect.
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

Add the new members directly after it:

```csharp
    // Suspend blanks the controller's display immediately; resume restores it. The
    // controller keeps its own RAM state (labels/icons/last volume) across a sleep that
    // doesn't cut its power, so un-blanking alone is enough — no resync needed here. If
    // the controller *did* lose power and reboot, the existing reconnect watchdog
    // (CheckConnection) independently detects the port reappearing and resyncs on its own.
    // SystemEvents raises on its own internal thread, not the UI thread, hence the dispatch.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    if (_serial.IsConnected) _serial.SendScreenOff();
                    break;
                case PowerModes.Resume:
                    if (_serial.IsConnected) _serial.SendScreenOn();
                    break;
            }
        });
    }

    // Best-effort blank on OS shutdown/logoff. Called directly by MainWindow's
    // WM_ENDSESSION handler, which already runs on the UI thread — no dispatch needed.
    public void SendScreenOff()
    {
        if (_serial.IsConnected) _serial.SendScreenOff();
    }
```

- [ ] **Step 4: Build**

Quit Dialed from the tray if it's running, then:

```bash
dotnet build Dialed.csproj -p:Platform=x64 -c Debug
```

Expected: `Build succeeded. 0 Error(s)`. If it instead fails with `Microsoft.Win32.SystemEvents` /
`PowerModeChangedEventArgs` / `PowerModes` not found, `System.Drawing.Common`'s transitive dependency
didn't pull in the type this time — add an explicit package reference and rebuild:

```bash
dotnet add Dialed.csproj package Microsoft.Win32.SystemEvents --version 8.0.0
dotnet build Dialed.csproj -p:Platform=x64 -c Debug
```

- [ ] **Step 5: Commit**

```bash
git add Core/ViewModels/MainViewModel.cs Dialed.csproj
git commit -m "app: blank the controller display on PC suspend/resume"
```

(Include `Dialed.csproj` in the `git add` only if Step 4's fallback was needed — otherwise the file has
no changes and `git add` is a no-op for it.)

---

## Task 5: App — blank on shutdown/logoff (`WM_ENDSESSION`)

**Files:**
- Modify: `MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MainViewModel.SendScreenOff()` from Task 4, via the existing `public MainViewModel ViewModel
  { get; }` property (`MainWindow.xaml.cs:93`).

- [ ] **Step 1: Call it before `ExitApp()` in the `WM_ENDSESSION` case**

In `MainWindow.xaml.cs`, find:

```csharp
                case WM_ENDSESSION:
                    // wParam == 0 means the shutdown was cancelled elsewhere.
                    if (wParam != IntPtr.Zero)
                        ExitApp();
                    return IntPtr.Zero;
```

Change to:

```csharp
                case WM_ENDSESSION:
                    // wParam == 0 means the shutdown was cancelled elsewhere.
                    if (wParam != IntPtr.Zero)
                    {
                        ViewModel.SendScreenOff();
                        ExitApp();
                    }
                    return IntPtr.Zero;
```

- [ ] **Step 2: Build**

Quit Dialed from the tray if it's running, then:

```bash
dotnet build Dialed.csproj -p:Platform=x64 -c Debug
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add MainWindow.xaml.cs
git commit -m "app: blank the controller display on shutdown/logoff"
```

---

## Task 6: Manual end-to-end verification (real hardware)

No test project exists in this repo (per `CLAUDE.md`), and none of Tasks 1–5 exercise the serial link
against real firmware — this task is the first point the whole chain is actually exercised together.
There is no code to write; each step is a manual check against the compiled/flashed result of Tasks 1–5.

- [ ] **Step 1: Flash the updated firmware**

Flash `Arduino/mixer` (as compiled in Task 2) onto the ESP32 controller, either via the Arduino IDE, or:

```bash
arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer --upload -p <COM_PORT>
```

Confirm the app's Settings page (or a `ver?` probe) reports `fw:esp32-mixer:1.2.0`.

- [ ] **Step 2: Verify suspend blanks the screen**

With Dialed running and the controller connected and showing its normal idle/active screen, put the PC
to sleep (Start menu → Power → Sleep). Expected: the screen goes solid black within roughly a second —
confirm it's not just showing a black idle frame by checking there's no residual arc/dot/icon visible and
no idle-GIF animation playing.

- [ ] **Step 3: Verify resume restores the screen immediately**

Wake the PC. Expected: within about a second of the display driver reconnecting, the controller screen
redraws with the idle animation. This is expected even if an active knob's volume screen was showing right
before sleep — `displayEnterIdle()` isn't gated on `blankMode`, so the idle timeout (a few seconds) elapses
in the background while blanked and the mode flips to idle before wake; on any sleep longer than the idle
timeout, resume always shows the idle animation, not the last active knob's screen. That's expected,
harmless behavior, not a bug — don't record it as a failure. No knob touch should be required either way.

- [ ] **Step 4: Verify a knob turn during suspend doesn't wake the screen**

Put the PC to sleep again. While it's asleep, physically turn a knob on the controller. Expected: the
screen stays blank (does not flash to an active-knob view). Resume the PC and confirm the screen redraws
normally on resume regardless.

- [ ] **Step 5: Verify shutdown blanks the screen**

With the PC running and the controller connected, shut Windows down fully (not sleep). Expected: the
screen goes black before the controller loses its own power (if it's on independent/hub power — if it's
bus-powered directly from the PC and loses power at the same time, this step reduces to "no crash/garbage
on screen," which is the pre-existing baseline behavior).

- [ ] **Step 6: Verify boot after shutdown restores normally**

Boot the PC back up and launch Dialed. Expected: the controller reaches its normal idle/active screen
through the app's regular connect/sync flow, same as any other cold boot — no leftover blank state.

- [ ] **Step 7: Verify manual "Quit" does NOT blank the screen**

With Dialed running and the controller showing its normal idle screen, right-click the tray icon → Quit
(or close the window → "Quit" in the dialog). Expected: the controller's screen is unaffected — it keeps
its current idle/active view (or, after `idleTimeoutMs` elapses with the app no longer sending data, the
firmware's existing idle-GIF fallback — not a blank screen).

- [ ] **Step 8: Record the outcome**

If all six behavioral checks (Steps 2–7) pass, the feature is complete. If any fail, note which step and
the observed vs. expected behavior — that's a bug in the relevant task above (display gating for Steps
2–4, `WM_ENDSESSION` wiring for Step 5, connect/sync flow for Step 6, or scope-creep into `ExitApp()` for
Step 7), not a new task.
