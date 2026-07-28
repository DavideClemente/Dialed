# Dialed — ESP32 Pin Layout

Wiring reference for the `mixer/` firmware on an **ESP32-WROOM DevKit** (38-pin).
Covers the round GC9A01 display, the rotary encoders, and the SPDT output toggle.

All device **`+`/`VCC`** lines share one **3.3 V** rail and all **`GND`** lines share one
ground rail. Only the signal pins below are per-device.

---

## Display — GC9A01 (SPI)

The panel is 4-wire SPI. Modules often silk-screen the data pins `SDA`/`SCL` (I²C-style
names) — they are MOSI/SCLK, **not** I²C.

| Display pin | ESP32 GPIO | Notes |
|-------------|:----------:|-------|
| `SCL` (SCLK) | **18** | Serial clock |
| `SDA` (MOSI) | **23** | Serial data |
| `CS`  | **14** | Chip select |
| `DC`  | **27** | Data/command |
| `RST` | **4**  | Reset |
| `VCC` | 3.3 V  | Logic is 3.3 V |
| `GND` | GND    | |
| `BL`  | 3.3 V  | Only if present and the screen stays dark |

Config lives in `mixer/User_Setup.h` (copy it into your TFT_eSPI **library** folder).

---

## Rotary encoders

Each encoder uses three signals: **CLK**, **DT** (quadrature) and **SW** (push switch).
Encoder `+`→3.3 V, `GND`→GND on the shared rails.

### Current functional layout — 4 encoders

| Encoder | CLK | DT | SW | Notes |
|---------|:---:|:--:|:--:|-------|
| **1** (`knob1`) | 17 | 16 | 5  | GPIO 5 is a strapping pin — don't hold this button at power-on |
| **2** (`knob2`) | 19 | 13 | 21 | |
| **3** (`knob3`) | 22 | 25 | 26 | |
| **4** (`knob4`) | 32 | 33 | 15 | GPIO 15 is a strapping pin — don't hold this button at power-on |

All 12 pins have internal pull-ups, so no external resistors are needed. Configure the
list in `encoders[]` at the top of `mixer/knobs.cpp`.

### Optional 5th encoder — the board's ceiling

A 5th encoder fits, but only on the three **input-only** pins, which have **no internal
pull-up**. `INPUT_PULLUP` does nothing on these, so pull-ups must come from elsewhere.

| Encoder | CLK | DT | SW | Notes |
|---------|:---:|:--:|:--:|-------|
| **5** (`knob5`) | 35 | 36 | 39 | Input-only — needs external / module pull-ups |

Pull-up requirement for GPIO 35 / 36 / 39:

- **KY-040 modules** already pull up CLK and DT on the board → those two just work.
  Add **one 10 kΩ resistor from GPIO 39 → 3.3 V** for the switch.
- **Bare EC11 encoders** (no module PCB): add **three 10 kΩ resistors**, one from each of
  35 / 36 / 39 → 3.3 V.

After encoder 5 the board is out of usable GPIO (see the budget below).

---

## Output toggle — SPDT (latching)

A 3-lug toggle: center = **COM**, the two outer lugs are the throws. Wired so the pin is
driven both ways — **no pull-up needed**, which is why it lives on an input-only pin.

| Toggle lug | Connection |
|------------|------------|
| COM (center) | **GPIO 34** |
| One outer lug | 3.3 V |
| Other outer lug | GND |

Emits `switch:0` / `switch:1` to the PC (output A/B). Handled in `mixer/knobs.cpp`
(`switchLoop`) and on the PC by `SerialManager`.

---

## Full pin budget (ESP32-WROOM DevKit)

| GPIO | Assignment |
|:----:|------------|
| 4  | Display RST |
| 5  | Enc1 SW *(strapping)* |
| 13 | Enc2 DT |
| 14 | Display CS |
| 15 | Enc4 SW *(strapping)* |
| 16 | Enc1 DT |
| 17 | Enc1 CLK |
| 18 | Display SCLK |
| 19 | Enc2 CLK |
| 21 | Enc2 SW |
| 22 | Enc3 CLK |
| 23 | Display MOSI |
| 25 | Enc3 DT |
| 26 | Enc3 SW |
| 27 | Display DC |
| 32 | Enc4 CLK |
| 33 | Enc4 DT |
| 34 | Toggle COM *(input-only)* |
| 35 | Enc5 CLK *(input-only, ext. pull-up)* |
| 36 | Enc5 DT  *(input-only, ext. pull-up)* |
| 39 | Enc5 SW  *(input-only, ext. pull-up)* |

### Do not use

| GPIO | Reason |
|:----:|--------|
| 6–11 | Connected to the SPI flash — using them crashes the chip |
| 1, 3 | UART0 — the USB serial link to the PC |
| 12 | Strapping (MTDI): an internal pull-up here **prevents boot** |
| 0, 2 | Strapping (and GPIO 2 = onboard LED): boot/flash-sensitive, avoid for inputs |

**Ceiling:** with the display + toggle, this board maxes out at **5 encoders**. Going
beyond needs an I²C GPIO expander (e.g. MCP23017) or an ESP32-S3 module that breaks out
more pins.
