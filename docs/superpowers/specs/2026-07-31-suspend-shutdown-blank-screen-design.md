# Design: Blank the controller display on PC suspend/shutdown

**Date:** 2026-07-31
**Status:** Approved (pending spec review)
**Scope:** When Windows suspends or shuts down, the ESP32 controller (`Arduino/mixer/`) should blank its
display instead of falling into its normal animated idle screen. Addresses
[DavideClemente/Dialed#18](https://github.com/DavideClemente/Dialed/issues/18).

## Problem

The firmware already has an idle-GIF fallback: after ~3s with no knob activity it shows an animated idle
screen (`displayEnterIdle()` / the idle GIF, see [display.cpp](../../../Arduino/mixer/display.cpp)). But
that fallback fires for two very different situations — "PC is on, user just isn't touching a knob" and
"PC is asleep or off" — and shows the same lively animation for both. There is currently no signal, on
either side of the serial link, that distinguishes "PC gone" from "PC idle." The app has no PC
power-state detection at all today (no `SystemEvents.PowerModeChanged`, no session-change hook).

## Goals

- While the PC is suspended or shutting down, the controller shows a blank (black) screen instead of the
  idle animation.
- As soon as the PC resumes or the app reconnects, the controller immediately returns to its normal
  display (idle or active knob) — no waiting for a fresh knob touch.
- Scope: ESP32 (`Arduino/mixer/`, GC9A01 round display) only. `mixer_nano/` (OLED) and the knobs-only
  `arduino/` sketch are untouched.

## Non-goals

- The Arduino Nano/OLED build (`mixer_nano/`) — out of scope for this pass.
- Blanking the screen when the user manually quits the Dialed app via the tray "Quit" dialog while
  Windows keeps running. That's not a PC suspend/shutdown; the existing behavior (fall back to the normal
  idle animation) is unchanged.
- Physical backlight power control. `BL` is hardwired to the 3.3V rail (see `Arduino/PINOUT.md`), not to a
  GPIO, and the documented 4-encoder build already uses every safe GPIO on the board (see the pin budget
  table in `PINOUT.md`) — there is no spare pin to route `BL` through without either dropping an encoder
  or repurposing a strapping pin (0/2), which risks breaking boot-mode selection. See "Blank screen
  content" below for what's achievable without a wiring change.

## Decisions (from brainstorming)

| Decision | Choice |
|---|---|
| Detection mechanism | App-side: `SystemEvents.PowerModeChanged` (suspend/resume) + existing `WM_ENDSESSION` handler (shutdown/logoff) |
| Protocol | New explicit command pair: `screen:off` / `screen:on` |
| Blank screen content | Solid black *and* panel sleep (GC9A01 `DISPOFF`+`SLPIN`) — see "Backlight" below |
| Backlight | Left as-is (hardwired to 3.3V, stays lit). No wiring change; see Non-goals |
| Firmware scope | `Arduino/mixer/` (ESP32) only |
| Wake behavior | Immediate — screen returns to normal as soon as `screen:on` arrives and the panel's mandatory ~120ms wake settle time elapses; no waiting for a knob touch |
| Manual app "Quit" | Not treated as suspend/shutdown; screen keeps its current idle-fallback behavior |

**Backlight decision:** offered the option of rewiring `BL` from the 3.3V rail to a GPIO for a true
backlight cutoff (either sacrificing an encoder to free a pin, or repurposing a strapping pin). Rejected
both — the software-only path (panel sleep + black fill) needs no wiring change, works on every board
already in the field as documented in `PINOUT.md`, and still meaningfully reduces panel power draw by
powering down the GC9A01's internal driving circuitry. The backlight LED itself stays lit; the visible
result is a dim, uniformly dark circle rather than true darkness.

Alternative considered and rejected: a firmware-only heuristic (blank after a much longer stretch of
serial silence, no new protocol command). Rejected because it can't distinguish "PC on, app just quiet"
from "PC asleep," needs a second arbitrarily-tuned timeout stacked on the existing idle timeout, and only
reacts after a lag instead of the instant the PC actually suspends.

## Architecture

### Protocol addition

New PC → Board lines, parsed only by the ESP32 tier (matches the existing README split between
"display tiers" and "knobs-only/Nano ignore what they can't use"):

- `screen:off` — blank the display and freeze it until further notice.
- `screen:on` — un-blank; resume normal rendering on the next tick.

### Firmware (`Arduino/mixer/`)

`display.cpp` / `display.h`:

- New `static bool blankMode` and public `void displayBlank(bool blank)`:
  - `true`: `tft.fillScreen(TFT_BLACK)`, then send the GC9A01 into its low-power state via raw commands
    (`tft.writecommand(0x28)` DISPOFF, then `tft.writecommand(0x10)` SLPIN — sent as raw command bytes
    rather than TFT_eSPI driver constants, since those aren't guaranteed defined for every driver header).
    Set `blankMode = true`.
  - `false`: reverse the sequence — `tft.writecommand(0x11)` SLPOUT, then a mandatory `delay(120)` before
    any further panel command (datasheet-required wake settle time — same pattern as the existing
    `delay(700)` in `displayUploadEnd()`), then `tft.writecommand(0x29)` DISPON. Clear `blankMode`, set
    `idleDirty = true` and `appDirty = true` so whichever mode is active redraws cleanly on the very next
    `displayTick()`.
- `displayTick()` gains an early return when `blankMode` is set — the same shape as the existing
  `uploadMode` gate that already "owns the display" during a GIF upload. No idle-GIF ticking, no active-arc
  animation happens while blank.
- `displayShowKnob()` / `displayShowMute()` also no-op (return immediately) while `blankMode` is set. This
  matters because these are called both from PC `vol:`/`mute:` lines *and* directly from local knob
  hardware events (`onKnobChange` in `mixer.ino`) — without this guard, physically turning a knob while
  the PC is asleep would silently re-arm `appDirty`/`activeKnob` and paint the active screen the instant
  blank mode ends, even though nothing legitimate changed.

`mixer.ino`:

- New `handleScreenLine(const char* line)`, parsing `screen:off` → `displayBlank(true)` and `screen:on` →
  `displayBlank(false)`, wired into `readIncomingSerial()` alongside the other line handlers.
- No changes to the idle-timeout bookkeeping (`lastKnobActivity`/`isIdle`) — it keeps running harmlessly in
  the background; it just has no visible effect while `displayTick()` is gated.

`version.h`: bump `FW_VERSION` (SemVer) in the same commit, per repo convention.

### App

`Core/SerialManager.cs` — two new one-line writers matching the existing style (`SendMute`,
`SendShowPercent`):

```csharp
public void SendScreenOff() { if (!_port.IsOpen) return; try { _port.WriteLine("screen:off"); } catch { } }
public void SendScreenOn()  { if (!_port.IsOpen) return; try { _port.WriteLine("screen:on");  } catch { } }
```

`Core/ViewModels/MainViewModel.cs`:

- Subscribe to `Microsoft.Win32.SystemEvents.PowerModeChanged` in the constructor. Never explicitly
  unsubscribed — `MainViewModel` is a process-lifetime singleton and nothing else in the class disposes
  cleanly either (the refresh/connection timers are never stopped, the tray icon is disposed only as part
  of process exit).
- Handler marshals onto `_dispatcherQueue.TryEnqueue(...)` (matching every other cross-thread serial/event
  callback in this class — `SystemEvents` raises on its own internal thread, not the UI thread):
  - `PowerModes.Suspend` → if `_serial.IsConnected`, `_serial.SendScreenOff()`.
  - `PowerModes.Resume` → if `_serial.IsConnected`, `_serial.SendScreenOn()`.
- No explicit resync is triggered on resume. Reasoning: if the controller stayed powered through sleep
  (common when it's on a powered hub or the host doesn't cut VBUS in modern standby), its RAM state
  (`knobLabel`/`knobIcon`/`targetVol`) is untouched, so un-blanking alone is enough to show the correct
  screen. If the controller *did* lose power and rebooted, the existing reconnect watchdog
  (`CheckConnection` in `MainViewModel.cs`) independently detects the port disappearing and reappearing
  and calls `ScheduleResync()` — that path is unchanged and already correct.
- New public passthrough `SendScreenOff()` on `MainViewModel`, for `MainWindow` to call from outside.

`MainWindow.xaml.cs` — in the existing `WM_ENDSESSION` case, right before the existing `ExitApp()` call
(only when `wParam != IntPtr.Zero`, i.e. the shutdown wasn't cancelled — same guard already there), add
`ViewModel.SendScreenOff()`. Best-effort; wrapped by the same try/catch already inside `SerialManager`. No
symmetric `screen:on` is needed here — the next app launch's normal connect/sync flow implicitly wakes the
display just by talking to the controller again.

`Arduino/README.md` — document the new `screen:off`/`screen:on` lines in the shared protocol section.

## Data flow summary

```
PC suspends  → SystemEvents.PowerModeChanged(Suspend) → SendScreenOff() → "screen:off" → displayBlank(true)
                 → fillScreen(BLACK) + DISPOFF + SLPIN (panel driver powered down; backlight stays lit)
PC resumes   → SystemEvents.PowerModeChanged(Resume)  → SendScreenOn()  → "screen:on"  → displayBlank(false)
                 → SLPOUT + 120ms settle + DISPON, then normal redraw on the next tick
PC shuts down → WM_ENDSESSION → ViewModel.SendScreenOff() → "screen:off" → displayBlank(true) → ExitApp()
Device lost power during sleep → screen already dark (no power) → reboots on resume → existing
  reconnect watchdog detects port + resyncs → firmware boots into its normal fresh-boot idle state
```

## Testing

No test project in this repo (per `CLAUDE.md`). Verification is manual:

- `arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer` to confirm the firmware still builds.
- Flash a real board, put the PC to sleep, confirm the screen blanks; resume, confirm it immediately
  redraws (idle animation if untouched, or the active knob screen if one was showing before sleep).
- Trigger a real shutdown/reboot cycle and confirm the screen blanks before power is lost (best-effort —
  depends on how much time Windows gives `WM_ENDSESSION` handlers and on the controller's own power
  source).
- Confirm a manual tray "Quit" still falls back to the normal idle animation, not blank.
