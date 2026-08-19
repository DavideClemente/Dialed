# Output-switch screen animation — design

**Date:** 2026-07-30
**Status:** approved, ready for planning

## Problem

The controller's GC9A01 has four screens today: volume (`displayShowKnob`), mute
(`displayShowMute`), idle (breathing ring or a stored GIF), and GIF-upload progress
(`displayUploadBegin`/`Progress`/`End`).

Flipping the physical SPDT emits `switch:0`/`switch:1` from `switchLoop()` in
`Arduino/mixer/knobs.cpp`, and the app re-routes the Windows default endpoint in
`OutputViewModel.Activate`. Nothing is ever sent back to the device, and the firmware
never reacts locally, so the display keeps showing whatever it was showing. The user
gets no confirmation of where their audio just went.

This adds a fifth screen: an output card that slides in when the output changes.

## Decisions

| Decision | Choice |
|---|---|
| What the card shows | Position marker **and** the real Windows endpoint name |
| Transition | Vertical slot slide, direction matching the toggle's throw |
| Triggers | Hardware toggle **and** in-app card taps (including reassigning the *active* position's device via its dropdown, which re-routes and re-fires `Activate()` too); not poll-detected default changes |
| Dwell | Reuses the existing `cfg:idle:<ms>` knob idle timeout |
| Failure | Explicit red state on screen; "no PC connected" stays neutral |

The card is drawn locally the instant the toggle moves — it does not wait for the PC —
so it still works with Dialed closed. The PC's confirmation only enriches or flags it.

## Protocol

The pair mirrors the knob protocol: a *push* line carrying assignment data, and an
*event* line carrying what just happened. Both are outbound-only (PC → device);
`SerialManager.HandleCommand` needs no new inbound parsing.

### `outset:<pos>:<kind>:<name>` — assignment push

The analogue of `assign:`.

- `pos` — `a` or `b`
- `kind` — `h` (headphone) or `s` (speaker), decided by the existing `GlyphFor`
  heuristic in `Core/ViewModels/OutputViewModel.cs`
- `name` — the endpoint's friendly name, CR/LF stripped, truncated to 31 chars

Sent on connect and whenever `SelectedDeviceA`/`SelectedDeviceB` changes. The firmware
stores it in `outLabel[2][32]` / `outKind[2]` in `assignments.cpp`, RAM-only, exactly as
`knobLabel` is.

### `out:<pos>:<state>` — the switch event

The analogue of `vol:`.

- `state` — `ok`, `none` (nothing assigned to that position), or `fail`
  (`OutputManager.SetDefault` returned false)

**`outset:` is safe to send on connect; `out:` is not.** An `out:` line wakes the
display, so it must only ever follow a real switch. This is the same discipline as the
existing "don't echo `vol:` on connect" rule in CLAUDE.md.

Splitting push from event is what lets the firmware render a *complete* card — correct
name, correct glyph — with zero round-trip latency.

## Firmware rendering

A fourth mode joins `ACTIVE` / `IDLE` in `Arduino/mixer/display.cpp`:

```c
enum OutState { OUT_PENDING, OUT_OK, OUT_NONE, OUT_FAIL };
void displayShowOutput(int pos, uint8_t state);
```

`displayShowOutput` decides internally whether to slide or repaint: a different position
(or arriving from another mode) slides; the same position already on screen repaints in
place.

### Layout

- **Ring** — full sweep at the outer radius the volume screen already uses
  (`ARC_R ± ARC_W/2`), drawn once via `drawSmoothArc`. `ACCENT_DEFAULT` (0x065F) for
  `PENDING`/`OK`, the upload screen's soft red (0xF9A6) for `NONE`/`FAIL`. The ring alone
  signals whether the switch took. It does not use `accent()` — that reads
  `knobColor[activeKnob]`, and the output card belongs to no knob.
- **Card band** — `y = 58..162`, full width. Contains a 48×48 glyph and the device name
  in font 2 below it. Everything that changes lives in the band, so the slide moves the
  whole card as one unit. (An earlier revision also drew the A/B position letter beneath
  the name; dropped after on-device testing showed it added nothing the user didn't
  already know from the physical toggle's position.)
- **Glyphs** — two 48×48 RGB565 bitmaps (headphone, speaker) in generated
  `Arduino/mixer/headphone_icon.h` and `Arduino/mixer/speaker_icon.h`, produced
  the same way `mute_icon.h` was, via `Arduino/tools/emoji_to_progmem.py`. ~9 KB
  of flash.

### The slide

A transient `TFT_eSprite` the size of the band (240×104, ~50 KB) is created when the
animation starts and `deleteSprite`d when it ends, so steady-state RAM is unchanged.
Each frame composes both the outgoing and incoming card into the sprite at the current
offset and pushes it once — no tearing, and no read-back from the panel (which this
wiring does not support).

- **Direction** — B always enters from below, A always from above, matching the toggle's
  throw. Deterministic regardless of which screen preceded it.
- **Duration** — ~300 ms on an ease-out curve, stepped from `displayTick` at the existing
  `ANIM_DT` (~60 fps). Non-blocking, so a knob turn mid-slide preempts it.
- **Fallback** — if `createSprite` returns null, skip the slide and draw the final card
  directly. The screen is still correct, just without the transition.

### State changes do not re-slide

When `out:` arrives after the local switch already animated, only the name line and the
ring repaint. `none` replaces the name with "Not assigned", `fail` with "Unavailable",
both in 0xF9A6. Firmware strings stay English, as the upload screen's "Updating" / "Done"
/ "Failed" already are — they do not go through `Loc.Get`.

On a cold boot with no `outset:` yet received, the name line shows a dash and the ring
stays `ACCENT_DEFAULT`. Absence of a PC is not an error state.

### Mode interactions

- `displayShowOutput` stops a playing idle GIF (`idleGifStop`) the way `displayShowKnob`
  does.
- It is ignored while `uploadMode` owns the display.
- Leaving the mode needs no change: `displayShowKnob` / `displayShowMute` already force a
  full redraw whenever `mode != ACTIVE`.

## Trigger wiring

### Firmware

- `knobs.h` gains `typedef void (*SwitchCallback)(int pos)` and
  `void knobsSetSwitchCallback(SwitchCallback cb)`. A separate setter rather than a wider
  `knobsSetup` signature, so the encoder/pot init path is untouched and the `mixer_nano`
  sketch is unaffected.
- `switchLoop()` invokes the callback immediately after it prints `switch:<n>`.
- In `mixer.ino`, that callback calls `displayShowOutput(pos, OUT_PENDING)` and stamps
  `lastKnobActivity = millis(); isIdle = false;` — so the card dwells for the user's
  configured `cfg:idle:<ms>` and then falls to idle through the existing loop check. No
  second timeout is introduced.
- A new `handleOutLine` in `mixer.ino` parses `out:` and does the same.
- `handleOutSetLine` lives in `assignments.cpp` beside `handleAssignLine` — storage only,
  no display calls. This mirrors how `handleAssignLine` stores and `handleVolumeLine`
  drives the screen.

### App

`SerialManager` gains:

```csharp
public void SendOutputAssignment(int position, bool isHeadset, string name);  // "outset:a:h:Name"
public void SendOutputSwitch(int position, string state);                     // "out:a:ok"
```

`OutputViewModel` does not take a `SerialManager`. It raises:

```csharp
public event Action<int, bool, string>? AssignmentChanged;  // pos, isHeadset, name
public event Action<int, string>? SwitchApplied;            // pos, "ok" | "none" | "fail"
```

`MainViewModel` subscribes and forwards to `serial`, matching how the VM already takes an
`Action _save` rather than the settings service. This keeps the VM testable and tolerant
of `serial` being null or reconnecting.

`Activate` raises `SwitchApplied` on every path — `none` when the position has no device,
`fail` when `SetDefault` returns false, `ok` otherwise. This covers both the hardware
toggle (via `ApplySwitchPosition`) and an in-app card tap (via `ActivateA`/`ActivateB`).

On connect, `MainViewModel` pushes `outset:` for both positions alongside its existing
channel sync. It never pushes `out:`.

`SyncActiveFromDefault` deliberately raises nothing, keeping default-device changes made
outside Dialed — which are poll-detected rather than intentional, and may not map to
either position — off the display.

### Edge case: flipping back to the already-active position

`ApplySwitchPosition` early-returns when the target position is already live, to avoid a
pointless re-route. It must **still** raise `SwitchApplied(pos, "ok")`. Without this, a
toggle flipped to the position Windows is already on would animate locally and then never
receive confirmation, leaving the device on a `PENDING` card.

## Out of scope

The `out:` line carries no `assign:`-style icon payload, so the glyph is one of two
built-in bitmaps rather than the real endpoint icon. Matching the knob screens' per-app
icons would mean a second icon-upload path for two rarely-changing devices.

## Verification

There is no test project; verification is build plus on-device.

- `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`. Quit Dialed from the tray
  first — otherwise the build reports success while the running exe is never replaced.
- Firmware compiles via `Arduino/tools/build-firmware.ps1` and the `firmware.yml` Actions
  job.
- `FW_VERSION` in `Arduino/mixer/version.h` goes `1.1.0` → `1.2.0` in the same commit as
  the firmware change, since this adds protocol lines.

Manual device checks:

1. Toggle in both directions — card slides the matching way, correct name and glyph.
2. Tap both cards in the app — the display animates too.
3. Flip to a position with nothing assigned — red ring, "Not assigned".
4. Flip back to the already-active position — lands on `ok`, not a stuck `PENDING`.
5. Toggle with Dialed closed — the card still slides, showing the last `outset:` name, or
   a dash on a cold boot.
6. Turn a knob mid-slide — the volume screen preempts cleanly.
7. Toggle during a GIF upload — ignored, upload screen keeps the display.
