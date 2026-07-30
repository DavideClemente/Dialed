# Output-Switch Screen Animation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a fifth controller screen — an output card that slides in when the default Windows playback endpoint changes, driven by the hardware toggle or an in-app card tap.

**Architecture:** Two new outbound serial lines mirror the knob protocol: `outset:` pushes assignment data (name + glyph kind) the way `assign:` does, and `out:` reports a switch result the way `vol:` reports a knob turn. Because the assignment is already on the device, the firmware renders the complete card the instant the toggle moves — no round trip — and the PC's `out:` line only confirms it or flags a failure. The card lives in a `y = 58..162` band composed into a transient `TFT_eSprite` so it can slide without tearing.

**Tech Stack:** ESP32 Arduino sketch (`Arduino/mixer`), TFT_eSPI 2.5.43 against a GC9A01, .NET 8 / WinUI 3 with CommunityToolkit.Mvvm.

**Spec:** `docs/superpowers/specs/2026-07-30-output-switch-animation-design.md`

## Global Constraints

- Wire format, exact strings: `outset:<a|b>:<h|s>:<name>` and `out:<a|b>:<ok|none|fail>`. Both are PC → device only; `SerialManager.HandleCommand` gains no new inbound parsing.
- **`outset:` may be sent on connect. `out:` may NOT.** An `out:` line wakes the display, so it must only follow a real switch. This is the same rule as the existing "don't echo `vol:` on connect" comment in `MainViewModel.SyncChannel`.
- Positions are `a`/`b` on the wire and `0`/`1` in code — the same 1-based-wire / 0-based-code split knobs already use, one index lower.
- Device names are truncated to **31 chars + NUL** (`outLabel[2][32]`), matching `knobLabel`.
- Colors, exact values: `ACCENT_DEFAULT` = `0x065F` for `PENDING`/`OK`; `0xF9A6` (the upload screen's soft red) for `NONE`/`FAIL`. The output ring must NOT call `accent()` — that reads `knobColor[activeKnob]` and the output card belongs to no knob.
- Firmware strings stay **English and hardcoded** ("Not assigned", "Unavailable"), as "Updating"/"Done"/"Failed" already are. They do not go through `Loc.Get`.
- `Arduino/mixer/version.h` `FW_VERSION` MUST go `1.1.0` → `1.2.0` in the same commit as the firmware change. `.github/workflows/firmware.yml` has a CI gate that fails the PR otherwise.
- There is no test project in this repo (`CLAUDE.md` states this). Verification is compile + on-device observation. Never claim a behavior works because the code "looks right".
- Quit Dialed from the tray before any `dotnet build` — otherwise the build reports success while the running exe is never replaced.

---

## File Structure

| File | Status | Responsibility |
| --- | --- | --- |
| `Arduino/mixer/headphone_icon.h` | Create | Generated 48×48 RGB565 headphone glyph. |
| `Arduino/mixer/speaker_icon.h` | Create | Generated 48×48 RGB565 speaker glyph. |
| `Arduino/mixer/assignments.h` | Modify | Declares output-position storage + `handleOutSetLine`. |
| `Arduino/mixer/assignments.cpp` | Modify | Storage + `outset:` parser. Storage only, no display calls. |
| `Arduino/mixer/display.h` | Modify | `OutState` enum + `displayShowOutput`. |
| `Arduino/mixer/display.cpp` | Modify | `OUTPUT` mode: ring, card band, sprite slide. |
| `Arduino/mixer/knobs.h` | Modify | `SwitchCallback` typedef + setter. |
| `Arduino/mixer/knobs.cpp` | Modify | Fires the callback on a real toggle move. |
| `Arduino/mixer/mixer.ino` | Modify | Switch callback → display; `out:` line handler. |
| `Arduino/mixer/version.h` | Modify | `FW_VERSION` → `1.2.0`. |
| `Core/SerialManager.cs` | Modify | `SendOutputAssignment` / `SendOutputSwitch`. |
| `Core/ViewModels/OutputViewModel.cs` | Modify | Raises `AssignmentChanged` / `SwitchApplied`. |
| `Core/ViewModels/MainViewModel.cs` | Modify | Forwards those events to serial; pushes `outset:` on resync. |

Unchanged on purpose: `Arduino/mixer_nano/*` (separate sketch, no display), `Core/OutputManager.cs`, `Core/Views/OutputPage.xaml`, the `Strings/*` resources (no new user-facing app strings).

---

### Task 1: Generate the two output glyphs

**Files:**
- Create: `Arduino/mixer/headphone_icon.h`
- Create: `Arduino/mixer/speaker_icon.h`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `HEADPHONE_ICON` / `SPEAKER_ICON` (`const uint16_t[2304] PROGMEM`, host-endian RGB565) plus `HEADPHONE_ICON_W`/`_H` and `SPEAKER_ICON_W`/`_H`, all `= 48`. Task 3 pushes these with `setSwapBytes(true)`.

`Arduino/tools/emoji_to_progmem.py` writes one symbol per file, so this is two files, exactly as `mute_icon.h` was produced.

- [ ] **Step 1: Fetch the two Noto Emoji PNGs**

Download from `https://github.com/googlefonts/noto-emoji/tree/main/png/72` into a scratch directory:
- `emoji_u1f3a7.png` — 🎧 headphone
- `emoji_u1f50a.png` — 🔊 speaker high volume

```bash
curl -L -o /tmp/emoji_u1f3a7.png https://raw.githubusercontent.com/googlefonts/noto-emoji/main/png/72/emoji_u1f3a7.png
```

```bash
curl -L -o /tmp/emoji_u1f50a.png https://raw.githubusercontent.com/googlefonts/noto-emoji/main/png/72/emoji_u1f50a.png
```

- [ ] **Step 2: Convert both to headers**

```bash
python Arduino/tools/emoji_to_progmem.py /tmp/emoji_u1f3a7.png Arduino/mixer/headphone_icon.h --size 48 --symbol HEADPHONE
```

```bash
python Arduino/tools/emoji_to_progmem.py /tmp/emoji_u1f50a.png Arduino/mixer/speaker_icon.h --size 48 --symbol SPEAKER
```

If Pillow is missing the script exits with a message — `pip install Pillow` first.

- [ ] **Step 3: Verify the generated headers**

Both files must declare `static const int HEADPHONE_ICON_W = 48;` (resp. `SPEAKER_ICON_W`) and a `[2304]` array. Confirm:

```bash
grep -c "0x" Arduino/mixer/headphone_icon.h && grep -n "_ICON_W\|_ICON\[" Arduino/mixer/headphone_icon.h Arduino/mixer/speaker_icon.h
```

Expected: 288 lines of hex per file (2304 values at 8 per line), and the `_W`/`_H`/array declarations present with the right symbol prefix in each.

- [ ] **Step 4: Commit**

```bash
git add Arduino/mixer/headphone_icon.h Arduino/mixer/speaker_icon.h && git commit -m "firmware: add headphone/speaker glyphs for the output card"
```

---

### Task 2: Output-position storage and the `outset:` parser

**Files:**
- Modify: `Arduino/mixer/assignments.h`
- Modify: `Arduino/mixer/assignments.cpp`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `OUT_POSITIONS` (= 2), `OUT_KIND_SPEAKER` (= 0), `OUT_KIND_HEADPHONE` (= 1), `extern char outLabel[OUT_POSITIONS][32]`, `extern uint8_t outKind[OUT_POSITIONS]`, and `bool handleOutSetLine(const char* line)` returning true when it consumed the line. Task 3 reads `outLabel`/`outKind`; Task 4 calls `handleOutSetLine`.

This file stores and never draws — same split as `handleAssignLine` (stores) versus `handleVolumeLine` in `mixer.ino` (drives the screen).

- [ ] **Step 1: Declare the storage in `assignments.h`**

Append below the existing `knob*` declarations, before the function declarations:

```c
// ── Output switch positions ─────────────────────────────────────────────────
// Position A/B assignments pushed by the PC as "outset:<a|b>:<h|s>:<name>".
// RAM-only, like knobLabel: a device reset drops them until the PC resyncs.
static const int OUT_POSITIONS = 2;

static const uint8_t OUT_KIND_SPEAKER   = 0;
static const uint8_t OUT_KIND_HEADPHONE = 1;

extern char    outLabel[OUT_POSITIONS][32];
extern uint8_t outKind [OUT_POSITIONS];
```

And add to the function declarations at the bottom:

```c
bool handleOutSetLine(const char* line);
```

- [ ] **Step 2: Define the storage in `assignments.cpp`**

Add below the existing `knobColor` definition at the top of the file:

```c
char    outLabel[OUT_POSITIONS][32] = {};
uint8_t outKind [OUT_POSITIONS]     = { OUT_KIND_SPEAKER, OUT_KIND_SPEAKER };
```

- [ ] **Step 3: Add the parser at the end of `assignments.cpp`**

```c
// Parse "outset:<a|b>:<h|s>:<DeviceName>"
// Note the guard order: "out:" cannot reach here because its 4th char is ':'
// while this line's is 's', so the two prefixes never collide.
bool handleOutSetLine(const char* line) {
  if (strncmp(line, "outset:", 7) != 0) return false;
  const char* p = line + 7;                       // "<a|b>:<h|s>:<Name>"

  int pos;
  if      (p[0] == 'a' || p[0] == 'A') pos = 0;
  else if (p[0] == 'b' || p[0] == 'B') pos = 1;
  else return false;
  if (p[1] != ':') return false;

  const char* kindStr = p + 2;                    // "<h|s>:<Name>"
  if (kindStr[0] == '\0' || kindStr[1] != ':') return false;
  outKind[pos] = (kindStr[0] == 'h' || kindStr[0] == 'H')
                 ? OUT_KIND_HEADPHONE : OUT_KIND_SPEAKER;

  strncpy(outLabel[pos], kindStr + 2, 31);
  outLabel[pos][31] = '\0';
  return true;
}
```

- [ ] **Step 4: Compile**

Run: `arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer`
Expected: compiles clean. Nothing calls `handleOutSetLine` yet — that is Task 4 — so expect no behavior change on device.

- [ ] **Step 5: Commit**

```bash
git add Arduino/mixer/assignments.h Arduino/mixer/assignments.cpp && git commit -m "firmware: store output-position assignments from outset: lines"
```

---

### Task 3: The output card and its slide animation

**Files:**
- Modify: `Arduino/mixer/display.h`
- Modify: `Arduino/mixer/display.cpp`

**Interfaces:**
- Consumes: `HEADPHONE_ICON`/`SPEAKER_ICON` and their `_W`/`_H` (Task 1); `outLabel`, `outKind`, `OUT_POSITIONS`, `OUT_KIND_HEADPHONE` (Task 2).
- Produces: `enum OutState { OUT_PENDING, OUT_OK, OUT_NONE, OUT_FAIL }` and `void displayShowOutput(int pos, uint8_t state)`. Task 4 calls it with `OUT_PENDING` from the local switch and with the parsed state from an `out:` line.

`displayShowOutput` decides internally whether to slide or repaint: a different position (or arriving from another mode) slides; the same position already on screen repaints the band in place.

- [ ] **Step 1: Declare the API in `display.h`**

`display.h` currently has no include and declares only functions. Add the include at the top (needed for `uint8_t`), then the new declarations after `displayEnterIdle()`:

```c
#pragma once
#include <Arduino.h>
```

```c
// Output-switch card. pos: 0 = A, 1 = B.
// OUT_PENDING = drawn locally the moment the toggle moves, before the PC has
// confirmed. The other three are what an "out:" line reports.
enum OutState : uint8_t { OUT_PENDING = 0, OUT_OK = 1, OUT_NONE = 2, OUT_FAIL = 3 };
void displayShowOutput(int pos, uint8_t state);
```

- [ ] **Step 2: Add includes, constants and state to `display.cpp`**

Add to the includes at the top:

```c
#include "headphone_icon.h"
#include "speaker_icon.h"
```

Extend the mode enum (existing line: `enum Mode { ACTIVE, IDLE };`):

```c
enum Mode { ACTIVE, IDLE, OUTPUT };
```

Add below the existing `TIP_DOT_R` constant:

```c
// ── Output card ───────────────────────────────────────────────────────────────
// Everything that changes between the two positions lives in one band, so the
// slide moves the whole card as a single sprite push. Band spans y=58..162:
// icon at +4 (48 tall), name centered at +66, position letter at +88.
static const int BAND_X = 0;
static const int BAND_Y = 58;
static const int BAND_W = 240;
static const int BAND_H = 104;
static const unsigned long OUT_SLIDE_MS = 300;

// Soft red, same value the upload screen uses for "Failed".
static const uint16_t OUT_RED = 0xF9A6;
```

Add below the existing `lastIdleMs` declaration:

```c
static int      outPos       = -1;    // card currently shown (-1 = none)
static int      outPrevPos   = -1;    // card sliding out (-1 = nothing to slide out)
static uint8_t  outState     = OUT_PENDING;
static bool     outDirty     = false; // full (re)entry: clear, draw ring, start the slide
static bool     outBandDirty = false; // state changed after the slide: repaint in place
static unsigned long outAnimStart = 0;

static TFT_eSprite outSpr   = TFT_eSprite(&tft);
static bool        outSprOK = false;
```

- [ ] **Step 3: Add the card drawing helpers to `display.cpp`**

Insert as a new section after `animateIdle()` and before the `── Public API ──` banner:

```c
// ── Output card ───────────────────────────────────────────────────────────────

static const char* outText(int pos, uint8_t state) {
  if (state == OUT_NONE) return "Not assigned";
  if (state == OUT_FAIL) return "Unavailable";
  if (pos >= 0 && pos < OUT_POSITIONS && outLabel[pos][0]) return outLabel[pos];
  return "-";   // no outset: received yet (cold boot). Not an error state.
}

static uint16_t outTextColor(uint8_t state) {
  return (state == OUT_NONE || state == OUT_FAIL) ? OUT_RED : TFT_WHITE;
}

// Full-sweep ring. Deliberately NOT accent(): that reads knobColor[activeKnob],
// and the output card belongs to no knob.
static void drawOutRing(uint8_t state) {
  uint16_t c = (state == OUT_NONE || state == OUT_FAIL) ? OUT_RED : ACCENT_DEFAULT;
  tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                    (uint32_t)ARC_A0, (uint32_t)(ARC_A0 + SWEEP), c, TFT_BLACK, true);
}

// Render one card into outSpr with its top edge at band-local y = dy. dy may be
// negative or past BAND_H — the sprite clips, which is what makes the slide work.
static void drawOutCard(int pos, int dy, uint8_t state) {
  const uint16_t* icon = (outKind[pos] == OUT_KIND_HEADPHONE) ? HEADPHONE_ICON : SPEAKER_ICON;
  // Both glyphs are generated at 48x48; host-endian RGB565 needs the byte swap,
  // same as the app-icon push in fullActiveRedraw.
  outSpr.setSwapBytes(true);
  outSpr.pushImage(BAND_W / 2 - HEADPHONE_ICON_W / 2, dy + 4,
                   HEADPHONE_ICON_W, HEADPHONE_ICON_H, icon);
  outSpr.setSwapBytes(false);

  outSpr.setTextDatum(MC_DATUM);
  outSpr.setTextSize(1);
  outSpr.setTextFont(2);
  outSpr.setTextColor(outTextColor(state), TFT_BLACK);
  outSpr.drawString(outText(pos, state), BAND_W / 2, dy + 66);

  outSpr.setTextFont(1);
  outSpr.setTextColor(ACCENT_DEFAULT, TFT_BLACK);
  outSpr.drawString(pos == 1 ? "B" : "A", BAND_W / 2, dy + 88);
}

// Same card straight to the panel. Used when the band sprite can't be allocated
// and for the in-place repaint when "out:" lands after the slide finished.
static void drawOutCardDirect(int pos, uint8_t state) {
  tft.fillRect(BAND_X, BAND_Y, BAND_W, BAND_H, TFT_BLACK);

  const uint16_t* icon = (outKind[pos] == OUT_KIND_HEADPHONE) ? HEADPHONE_ICON : SPEAKER_ICON;
  tft.setSwapBytes(true);
  tft.pushImage(CX - HEADPHONE_ICON_W / 2, BAND_Y + 4,
                HEADPHONE_ICON_W, HEADPHONE_ICON_H, icon);
  tft.setSwapBytes(false);

  tft.setTextDatum(MC_DATUM);
  tft.setTextSize(1);
  tft.setTextFont(2);
  tft.setTextColor(outTextColor(state), TFT_BLACK);
  tft.drawString(outText(pos, state), CX, BAND_Y + 66);

  tft.setTextFont(1);
  tft.setTextColor(ACCENT_DEFAULT, TFT_BLACK);
  tft.drawString(pos == 1 ? "B" : "A", CX, BAND_Y + 88);
}

// Free the band sprite. Called on every path that leaves OUTPUT mode — ~50 KB
// must not stay resident once the slide is over.
static void outReleaseSprite() {
  if (outSprOK) {
    outSpr.deleteSprite();
    outSprOK = false;
  }
}

// Per-frame step, called from displayTick at ANIM_DT while mode == OUTPUT.
static void outputTick(unsigned long now) {
  if (outDirty) {
    tft.fillScreen(TFT_BLACK);
    drawOutRing(outState);
    outSprOK     = (outSpr.createSprite(BAND_W, BAND_H) != nullptr);
    outAnimStart = now;
    outDirty     = false;
    outBandDirty = false;
    if (!outSprOK) {
      // No RAM for the band: skip the slide, draw the final card directly. The
      // screen is still correct, just without the transition.
      drawOutCardDirect(outPos, outState);
      outPrevPos = -1;
      return;
    }
  }

  if (outSprOK) {
    float t = (float)(now - outAnimStart) / (float)OUT_SLIDE_MS;
    if (t > 1.0f) t = 1.0f;
    float e = 1.0f - (1.0f - t) * (1.0f - t);   // ease-out quad

    // B enters from below, A from above — matching the toggle's throw, and
    // independent of whichever screen preceded the card.
    int dir = (outPos == 1) ? 1 : -1;
    int off = (int)(BAND_H * (1.0f - e) * dir);

    outSpr.fillSprite(TFT_BLACK);
    if (outPrevPos >= 0)
      drawOutCard(outPrevPos, off - BAND_H * dir, OUT_OK);
    drawOutCard(outPos, off, outState);
    outSpr.pushSprite(BAND_X, BAND_Y);

    if (t >= 1.0f) {
      outReleaseSprite();
      outPrevPos = -1;
    }
    return;
  }

  if (outBandDirty) {
    drawOutRing(outState);
    drawOutCardDirect(outPos, outState);
    outBandDirty = false;
  }
}
```

- [ ] **Step 4: Add `displayShowOutput` to the public API section**

Insert after `displayEnterIdle()` in `display.cpp`:

```c
// Call when the output changes — locally when the toggle moves (state =
// OUT_PENDING) or from an "out:" line once the PC has routed. Re-entry with the
// position already on screen repaints in place instead of re-sliding.
void displayShowOutput(int pos, uint8_t state) {
  if (pos < 0 || pos >= OUT_POSITIONS) return;
  if (uploadMode) return;   // the upload screen owns the display until it finishes
  if (gifMode) { idleGifStop(); gifMode = false; }

  if (mode == OUTPUT && pos == outPos) {
    outState     = state;
    outBandDirty = true;
    return;
  }

  outPrevPos   = (mode == OUTPUT) ? outPos : -1;
  outPos       = pos;
  outState     = state;
  mode         = OUTPUT;
  outDirty     = true;
}
```

- [ ] **Step 5: Release the sprite on every exit from OUTPUT mode**

Add `outReleaseSprite();` as the first statement after the early-return guard in each of the three functions that leave the mode.

In `displayEnterIdle`:

```c
void displayEnterIdle() {
  if (mode == IDLE) return;
  outReleaseSprite();
  mode      = IDLE;
  idleDirty = true;
}
```

In `displayShowMute`, immediately after the `gifMode` line:

```c
  if (gifMode) { idleGifStop(); gifMode = false; }
  outReleaseSprite();
```

In `displayShowKnob`, immediately after its `gifMode` line:

```c
  if (gifMode) { idleGifStop(); gifMode = false; }
  outReleaseSprite();
```

Neither `displayShowKnob` nor `displayShowMute` needs any other change: both already force a full redraw whenever `mode != ACTIVE`, which now covers `OUTPUT`.

- [ ] **Step 6: Drive the mode from `displayTick`**

`displayTick`'s body is currently `if (mode == ACTIVE) { … } else { // IDLE … }`. Change the `else` to an `else if (mode == OUTPUT)` branch followed by the existing idle block as a final `else`:

```c
  if (mode == ACTIVE) {
    // ... unchanged ...
  } else if (mode == OUTPUT) {
    if (now - lastAnimMs >= ANIM_DT) {
      lastAnimMs = now;
      outputTick(now);
    }
  } else { // IDLE
    // ... unchanged ...
  }
```

- [ ] **Step 7: Compile**

Run: `arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer`
Expected: compiles clean. Check the reported "Global variables use N bytes" line — it should be roughly unchanged from before this task, because the 50 KB band sprite is a heap allocation made only during a slide, not a static.

Nothing calls `displayShowOutput` yet (Task 4), so the device behaves exactly as before.

- [ ] **Step 8: Commit**

```bash
git add Arduino/mixer/display.h Arduino/mixer/display.cpp && git commit -m "firmware: add the output card screen with a sliding band"
```

---

### Task 4: Wire the switch and the `out:` line, bump the version

**Files:**
- Modify: `Arduino/mixer/knobs.h`
- Modify: `Arduino/mixer/knobs.cpp:179-190` (`switchLoop`)
- Modify: `Arduino/mixer/mixer.ino`
- Modify: `Arduino/mixer/version.h`

**Interfaces:**
- Consumes: `displayShowOutput`, `OUT_PENDING`/`OUT_OK`/`OUT_NONE`/`OUT_FAIL` (Task 3); `handleOutSetLine` (Task 2).
- Produces: `typedef void (*SwitchCallback)(int pos)` and `void knobsSetSwitchCallback(SwitchCallback cb)` in `knobs.h`. Nothing later consumes these — this task closes the firmware side.

A separate setter rather than widening `knobsSetup`'s signature, so the encoder/pot init path is untouched and the `mixer_nano` sketch (which has its own `knobs.h`) stays unaffected.

- [ ] **Step 1: Declare the callback in `knobs.h`**

Add beside the existing `KnobCallback` typedef and declarations:

```c
// Fired when the output toggle actually moves. NOT fired for the boot-time
// sample that merely announces the toggle's resting position — see switchLoop.
typedef void (*SwitchCallback)(int pos);
void knobsSetSwitchCallback(SwitchCallback cb);
```

- [ ] **Step 2: Fire it from `switchLoop` in `knobs.cpp`**

Add the storage beside the existing `s_cb`:

```c
static SwitchCallback s_switchCb = nullptr;

void knobsSetSwitchCallback(SwitchCallback cb) { s_switchCb = cb; }
```

Then replace the report block inside `switchLoop()`:

```c
  if (raw != swPos && (now - swReadChanged) > SWITCH_DEBOUNCE_MS) {
    // swPos < 0 means this is the first sample since boot: it tells the PC where
    // the toggle is resting, but the user did not touch anything, so it must not
    // wake the display (which has no assignment data yet and would show "-").
    bool initial = (swPos < 0);
    swPos = raw;
    Serial.print("switch:"); Serial.println(swPos);
    if (!initial && s_switchCb) s_switchCb(swPos);
  }
```

- [ ] **Step 3: Add the switch callback and `out:` handler to `mixer.ino`**

Add beside the existing `handleConfigLine`:

```c
static void handleOutLine(const char* line) {
  if (strncmp(line, "out:", 4) != 0) return;
  const char* p = line + 4;                       // "<a|b>:<state>"

  int pos;
  if      (p[0] == 'a' || p[0] == 'A') pos = 0;
  else if (p[0] == 'b' || p[0] == 'B') pos = 1;
  else return;
  if (p[1] != ':') return;

  const char* s = p + 2;
  uint8_t state;
  if      (strcmp(s, "ok")   == 0) state = OUT_OK;
  else if (strcmp(s, "none") == 0) state = OUT_NONE;
  else if (strcmp(s, "fail") == 0) state = OUT_FAIL;
  else return;                                    // unknown state: ignore the line

  displayShowOutput(pos, state);
  lastKnobActivity = millis();
  isIdle = false;
}
```

Add beside the existing `onKnobChange`:

```c
// The toggle moved. Draw the card immediately from stored assignment data — the
// PC's "out:" confirmation may never arrive (Dialed closed) and must not be
// waited on. Counts as activity, so the card dwells for cfg:idle:<ms> and then
// falls to the idle screen through the normal loop() check.
void onSwitchChange(int pos) {
  displayShowOutput(pos, OUT_PENDING);
  lastKnobActivity = millis();
  isIdle = false;
}
```

- [ ] **Step 4: Dispatch the two new lines and register the callback**

In `readIncomingSerial`, add both handlers to the parser chain:

```c
        if (!idleGifHandleLine(inLine)) {
          handleAssignLine(inLine);
          handleIconLine(inLine);
          handleVolumeLine(inLine);
          handleMuteLine(inLine);
          handleConfigLine(inLine);
          handleOutSetLine(inLine);
          handleOutLine(inLine);
          handleVerLine(inLine);
        }
```

In `setup()`, register the callback after `knobsSetup`:

```c
void setup() {
  displaySetup();
  knobsSetup(onKnobChange);   // knobsSetup calls Serial.begin(921600)
  knobsSetSwitchCallback(onSwitchChange);
  sendFirmwareVersion();      // announce firmware version to the PC on boot
  lastKnobActivity = millis();
}
```

- [ ] **Step 5: Bump `FW_VERSION`**

In `Arduino/mixer/version.h`:

```c
#define FW_VERSION "1.2.0"
```

CI (`.github/workflows/firmware.yml`) fails the PR if `Arduino/mixer` changed without this.

- [ ] **Step 6: Compile and flash**

Run: `arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer`
Expected: compiles clean.

Then flash the device and check the firmware half on its own, with Dialed closed:
- Flip the toggle → the card slides in (B from below, A from above), showing the position letter and `-` for the name (no `outset:` has been received since boot).
- The card stays for the configured idle timeout, then falls to the idle screen/GIF.
- Turn a knob mid-slide → the volume screen takes over cleanly, no leftover band.
- Reset the board without touching the toggle → the display stays idle. It must NOT show the card.

- [ ] **Step 7: Commit**

```bash
git add Arduino/mixer/knobs.h Arduino/mixer/knobs.cpp Arduino/mixer/mixer.ino Arduino/mixer/version.h && git commit -m "firmware: show the output card on toggle and out: lines"
```

---

### Task 5: `SerialManager` output lines

**Files:**
- Modify: `Core/SerialManager.cs` (beside `SendAssignment`, around line 120)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `void SendOutputAssignment(int position, bool isHeadset, string name)` and `void SendOutputSwitch(int position, string state)`. Task 6 calls both. `position` is 0 = A, 1 = B; `state` is one of `"ok"`, `"none"`, `"fail"`.

- [ ] **Step 1: Add both send methods**

Insert after `SendAssignment`:

```csharp
    // Pushes what's assigned to an output position. The analogue of SendAssignment:
    // the device stores it silently and stays on whatever screen it's showing, so
    // this is safe to send on connect. Its counterpart SendOutputSwitch is NOT.
    public void SendOutputAssignment(int position, bool isHeadset, string name)
    {
        if (!_port.IsOpen) return;
        try
        {
            var pos = position == 0 ? "a" : "b";
            var kind = isHeadset ? "h" : "s";
            var safeName = name.Replace("\r", "").Replace("\n", "");
            _port.WriteLine($"outset:{pos}:{kind}:{safeName}");
        }
        catch { }
    }

    // Reports the result of a switch. This DOES wake the device's screen (it shows
    // the output card), so it must only ever follow a real switch — never a connect
    // or a poll-detected default-device change. Same rule as SendVolume.
    // state: "ok" | "none" (nothing assigned to that position) | "fail" (routing failed).
    public void SendOutputSwitch(int position, string state)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"out:{(position == 0 ? "a" : "b")}:{state}"); }
        catch { }
    }
```

- [ ] **Step 2: Build**

Quit Dialed from the tray first, then run:

```bash
dotnet build Dialed.csproj -p:Platform=x64 -c Debug
```

Expected: `0 Error(s)`. Nothing calls these yet — that is Task 6.

- [ ] **Step 3: Commit**

```bash
git add Core/SerialManager.cs && git commit -m "feat: add outset:/out: serial lines for the output card"
```

---

### Task 6: Raise and forward the output events

**Files:**
- Modify: `Core/ViewModels/OutputViewModel.cs`
- Modify: `Core/ViewModels/MainViewModel.cs:157` (subscribe) and `:186-194` (`ScheduleResync`)

**Interfaces:**
- Consumes: `SendOutputAssignment` / `SendOutputSwitch` (Task 5).
- Produces: on `OutputViewModel` — `event Action<int, bool, string>? AssignmentChanged` (position, isHeadset, name) and `event Action<int, string>? SwitchApplied` (position, `"ok"`/`"none"`/`"fail"`); plus `void PushAssignments()`, which re-raises `AssignmentChanged` for both positions. Nothing later consumes these.

`OutputViewModel` raises events rather than taking a `SerialManager`, matching how it already takes an `Action _save` instead of the settings service — the VM stays independent of a handle that can be null, reconnecting, or mid-reflash.

- [ ] **Step 1: Declare the events and the glyph-kind helper in `OutputViewModel`**

Add below the existing `StatusText` property:

```csharp
    // Raised when a position's assigned device changes. MainViewModel forwards this
    // to the controller as an "outset:" line; the device stores it and keeps showing
    // whatever screen it's on.
    public event Action<int, bool, string>? AssignmentChanged;

    // Raised on every completed switch — hardware toggle or in-app card tap — with
    // "ok", "none" (nothing assigned) or "fail" (routing failed). MainViewModel
    // forwards this as an "out:" line, which wakes the device's screen.
    public event Action<int, string>? SwitchApplied;

    // The device only has two glyphs, so this collapses the same name heuristic
    // GlyphFor uses into the bool the wire format carries.
    private static bool IsHeadset(OutputDevice? device)
        => device is not null && GlyphFor(device) == char.ConvertFromUtf32(HeadphoneGlyph);
```

- [ ] **Step 2: Add `PushAssignments`**

Add below `RebuildExclusions`:

```csharp
    // Re-announces both positions. Called by MainViewModel after a (re)connect, so a
    // device that just booted gets its assignments back before the user touches the
    // toggle. A position with nothing assigned pushes an empty name, which the
    // firmware renders as "-".
    public void PushAssignments()
    {
        AssignmentChanged?.Invoke(0, IsHeadset(SelectedDeviceA), SelectedDeviceA?.Name ?? "");
        AssignmentChanged?.Invoke(1, IsHeadset(SelectedDeviceB), SelectedDeviceB?.Name ?? "");
    }
```

- [ ] **Step 3: Raise `AssignmentChanged` when a selection changes**

`RebuildExclusions` already runs on every selection change and on device add/remove, and already ends by re-raising the derived name/glyph properties. Add the push at the very end of it, after the existing `OnPropertyChanged(nameof(BIconGlyph));`:

```csharp
        OnPropertyChanged(nameof(AIconGlyph));
        OnPropertyChanged(nameof(BIconGlyph));

        PushAssignments();
```

`RebuildExclusions` also runs from `LoadDevices` during the constructor, before `MainViewModel` has subscribed — so that first raise reaches no handler and is simply dropped. That is fine and must not be "fixed": Step 7 pushes both positions explicitly on every resync, which is what actually gets the assignments onto a device.

- [ ] **Step 4: Raise `SwitchApplied` from every `Activate` path**

Replace the body of `Activate`:

```csharp
    private void Activate(OutputPosition position)
    {
        var device = position == OutputPosition.A ? SelectedDeviceA : SelectedDeviceB;
        var label = position == OutputPosition.A ? "A" : "B";
        var index = position == OutputPosition.A ? 0 : 1;

        if (device is null)
        {
            StatusText = Loc.Get("Output_AssignFirst", label);
            SwitchApplied?.Invoke(index, "none");
            return;
        }

        if (_output.SetDefault(device.Id))
        {
            ActivePosition = position;
            StatusText = Loc.Get("Output_Switched", device.Name);
            SwitchApplied?.Invoke(index, "ok");
        }
        else
        {
            StatusText = Loc.Get("Output_SwitchFailed", device.Name);
            SwitchApplied?.Invoke(index, "fail");
        }

        RefreshDefaults();
    }
```

- [ ] **Step 5: Report `ok` on the already-active no-op path**

`ApplySwitchPosition` early-returns when the target is already live, to avoid a pointless re-route. It must still report, or a toggle flipped to the position Windows is already on animates locally and then sits on a `PENDING` card forever:

```csharp
    // Driven by the hardware switch. Same no-op guard: if we're already on that
    // position the default already matches, so don't re-route — but still report
    // "ok", because the device has already drawn a pending card and is waiting to
    // have it confirmed.
    public void ApplySwitchPosition(int position)
    {
        var target = position == 0 ? OutputPosition.A : OutputPosition.B;
        if (ActivePosition == target)
        {
            SwitchApplied?.Invoke(position, "ok");
            return;
        }
        Activate(target);
    }
```

`SyncActiveFromDefault` is deliberately left alone: it runs off the 2 s poll, so a default-device change made in the Windows flyout must not wake the device's screen.

- [ ] **Step 6: Forward both events in `MainViewModel`**

At line 157, where `Output` is constructed, subscribe immediately after. The handlers read `_serial` lazily, so it does not matter that `_serial` is assigned four lines later:

```csharp
        Output = new OutputViewModel(_settings, _outputManager, () => SettingsService.Save(_settings));
        Output.AssignmentChanged += (pos, isHeadset, name) => _serial.SendOutputAssignment(pos, isHeadset, name);
        Output.SwitchApplied += (pos, state) => _serial.SendOutputSwitch(pos, state);
```

- [ ] **Step 7: Push assignments on resync**

In `ScheduleResync`, alongside the existing channel sync — `outset:` is safe here, `out:` would not be:

```csharp
    private void ScheduleResync() => _ = Task.Run(async () =>
    {
        await Task.Delay(2000);
        _dispatcherQueue.TryEnqueue(() =>
        {
            SyncAllChannels();
            Output.PushAssignments();
            _serial.RequestFirmwareVersion();
        });
    });
```

- [ ] **Step 8: Build**

Quit Dialed from the tray first, then run:

```bash
dotnet build Dialed.csproj -p:Platform=x64 -c Debug
```

Expected: `0 Error(s)`.

- [ ] **Step 9: Commit**

```bash
git add Core/ViewModels/OutputViewModel.cs Core/ViewModels/MainViewModel.cs && git commit -m "feat: push output assignments and switch results to the controller"
```

---

### Task 7: End-to-end verification on the device

**Files:** none — this task changes no code unless it finds a defect.

**Interfaces:**
- Consumes: everything from Tasks 1-6.
- Produces: nothing.

Run with the firmware from Task 4 flashed and the app from Task 6 running. Work through every check; record the actual observed result for each, not an expectation.

- [ ] **Step 1: Happy path, both directions**

Flip the toggle to A, then to B. Each time the card slides in — B from below, A from above — showing the real Windows endpoint name and the matching glyph (headphone vs speaker), with an accent-colored ring. Audio actually moves to that device.

- [ ] **Step 2: In-app card tap**

With the app's Output page open, tap the card for the position that is not currently active. The device animates the same way. Tapping the already-active card is a no-op in the app and must not animate.

- [ ] **Step 3: Unassigned position**

Clear the device assigned to position B in the app, then flip the toggle to B. The card slides in, then repaints in place with a red ring and "Not assigned" — with no second slide.

- [ ] **Step 4: Already-active position**

With A live, flip the toggle to B and back to A. The final card must settle showing A's name in white with an accent ring — not a stuck `-` or a pending-looking card. This is the `ApplySwitchPosition` no-op path from Task 6 Step 5.

- [ ] **Step 5: Dwell and exit**

After a switch, the card stays for the idle timeout configured in Settings, then falls to the idle screen (or the idle GIF, if one is uploaded). Change the idle timeout in Settings and confirm the card's dwell follows it.

- [ ] **Step 6: App closed**

Quit Dialed from the tray. Flip the toggle. The card still slides, still shows the name from the last `outset:` (the firmware keeps it in RAM until reset), and the ring stays accent — no red, because "no PC" is not an error.

- [ ] **Step 7: Preemption**

Flip the toggle and turn a knob during the ~300 ms slide. The volume screen takes over immediately, with no leftover band and no stray pixels where the card was.

- [ ] **Step 8: GIF upload**

Start an idle-GIF upload from the app and flip the toggle mid-transfer. The upload progress screen keeps the display; the card is ignored. The upload completes normally.

- [ ] **Step 9: Cold boot**

Reset the board with the app closed, then flip the toggle. The card shows `-` for the name, because no `outset:` has arrived since boot. Reconnect the app, wait for the resync, flip again — the real name is now there.

- [ ] **Step 10: Record results**

Report which checks passed and which failed, with what was actually observed. Fix any failures before this task is marked done.

---

## Known accepted behavior

If the board resets while the app is connected, the firmware's boot-time `switch:` announcement reaches the app, which routes and echoes `out:`, so the card appears briefly without the user touching anything. The firmware suppresses its own local draw for that sample (Task 4, Step 2), but it cannot suppress the app's echo without either a wire-format change or state tracking that misfires when the app requests a version with `ver?`. The card is short-lived and accurate, so this is accepted rather than worked around.
