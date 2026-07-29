#include "knobs.h"
#include <Arduino.h>

// Set to 1 to use rotary encoders, 0 to use potentiometers
#define USE_ENCODER 1

static KnobCallback s_cb = nullptr;

// ── Potentiometers ──────────────────────────────────────────────────────────

struct PotConfig { const char* id; int pin; };

static PotConfig pots[] = {
  { "knob1", 15 },
  { "knob2", 35 },
};
static const int NUM_POTS = sizeof(pots) / sizeof(pots[0]);

static float smoothed[NUM_POTS] = {};
static float lastSent[NUM_POTS];

// ── Encoders ────────────────────────────────────────────────────────────────

struct EncConfig { const char* id; int clkPin; int dtPin; int swPin; };

static EncConfig encoders[] = {
  { "knob1", 17, 16,  5 },
  { "knob2", 19, 13, 21 },
  { "knob3", 22, 25, 26 },
  { "knob4", 32, 33, 15 },
};
static const int NUM_ENCODERS = sizeof(encoders) / sizeof(encoders[0]);

// ── Output toggle switch (SPDT) ─────────────────────────────────────────────
// A latching 3-terminal toggle: COM -> SWITCH_PIN, the two throws wired to 3.3V
// and GND so the pin is driven HIGH in one position and LOW in the other. This
// lives on an input-only pin (34/35/36/39) on purpose: those have no internal
// pull-up, but none is needed here since both positions actively drive the line,
// which frees every pull-up-capable GPIO for the encoders. Emits "switch:0" /
// "switch:1" (handled by SerialManager.HandleLine on the PC side).
static const int SWITCH_PIN = 34;

static int           swPos          = -1;   // last reported position (-1 = unsent)
static int           swLastRead     = -1;   // last raw sample, for debounce
static unsigned long swReadChanged  = 0;    // when swLastRead last changed
static const unsigned long SWITCH_DEBOUNCE_MS = 30;

// encDelta accumulates *completed detents* (±1 each), not raw edges — the
// decoder below only bumps it on a valid transition that lands on a detent.
// encState holds each encoder's position in that state machine.
static volatile int     encDelta[NUM_ENCODERS] = {};
static volatile uint8_t encState[NUM_ENCODERS] = {};
static uint8_t          swLastState[NUM_ENCODERS];
static unsigned long    swLastDebounce[NUM_ENCODERS];

// ── Active knob count (runtime, pushed by the PC via "cfg:knobs:<N>") ─────────
// Defaults to the physical maximum so behavior is unchanged until the PC syncs.
// knobsLoop only samples/reports the first activeEncoders/activePots knobs, so
// pins mapped to higher (unwired) knobs never generate messages.
static int activeEncoders = NUM_ENCODERS;
static int activePots     = NUM_POTS;

void knobsSetActiveCount(int n) {
  if (n < 0) n = 0;
  activeEncoders = n < NUM_ENCODERS ? n : NUM_ENCODERS;
  activePots     = n < NUM_POTS     ? n : NUM_POTS;
  // Drop deltas accumulated on now-idle encoder pins so a later re-enable starts
  // clean instead of flushing a stale burst.
  noInterrupts();
  for (int i = 0; i < NUM_ENCODERS; i++) encDelta[i] = 0;
  interrupts();
}

// Press-suppression: while an encoder's switch is held (and for a short tail
// after), its rotation is discarded so a press that mechanically nudges the
// shaft can't emit a phantom volume step.
static unsigned long    rotSuppressUntil[NUM_ENCODERS] = {};
static const unsigned long ROT_SUPPRESS_MS = 60;

// ── Rotary decode: Ben Buxton state machine (half-step) ──────────────────────
// Emits a step only when the encoder completes a valid transition and settles on
// a detent. Partial/invalid transitions — contact bounce, press wobble,
// direction-reversal backlash — advance internal state but emit nothing, which
// removes the phantom steps the old per-edge (QEM / DETENT_DIV) decoder produced.
//
// This is the HALF-STEP table: it emits at both the 00 and 11 rest positions,
// because the encoders used here rest on a detent twice per quadrature cycle
// (the full-step table emitted only once per two physical clicks). If you fit an
// encoder that completes one full cycle per detent, use Buxton's full-step table
// instead or you will get one step per two clicks.
#define DIR_CW   0x10
#define DIR_CCW  0x20

#define R_START       0x0
#define R_CCW_BEGIN   0x1
#define R_CW_BEGIN    0x2
#define R_START_M     0x3
#define R_CW_BEGIN_M  0x4
#define R_CCW_BEGIN_M 0x5

static const uint8_t ttable[6][4] = {
  /* R_START (00)   */ { R_START_M,           R_CW_BEGIN,    R_CCW_BEGIN,  R_START },
  /* R_CCW_BEGIN    */ { R_START_M | DIR_CCW, R_START,       R_CCW_BEGIN,  R_START },
  /* R_CW_BEGIN     */ { R_START_M | DIR_CW,  R_CW_BEGIN,    R_START,      R_START },
  /* R_START_M (11) */ { R_START_M,           R_CCW_BEGIN_M, R_CW_BEGIN_M, R_START },
  /* R_CW_BEGIN_M   */ { R_START_M,           R_START_M,     R_CW_BEGIN_M, R_START | DIR_CW },
  /* R_CCW_BEGIN_M  */ { R_START_M,           R_CCW_BEGIN_M, R_START_M,    R_START | DIR_CCW },
};

static void IRAM_ATTR readEncoders() {
  for (int i = 0; i < NUM_ENCODERS; i++) {
    // pinstate = (CLK << 1) | DT, so a clockwise turn increases volume. Swap the
    // two reads here to flip direction for a differently-wired encoder.
    uint8_t pinstate = (digitalRead(encoders[i].clkPin) << 1) | digitalRead(encoders[i].dtPin);
    encState[i] = ttable[encState[i] & 0x0F][pinstate];
    uint8_t dir = encState[i] & 0x30;
    if (dir == DIR_CW)       encDelta[i]++;
    else if (dir == DIR_CCW) encDelta[i]--;
  }
}

// ── Public API ──────────────────────────────────────────────────────────────

void knobsSetup(KnobCallback cb) {
  s_cb = cb;
  // Larger RX buffer so a base64 GIF-upload chunk (~5.5 KB) can't overrun the
  // UART FIFO if loop() briefly stalls on a redraw. Must precede begin().
  Serial.setRxBufferSize(16384);
  Serial.begin(921600);

#if USE_ENCODER
  for (int i = 0; i < NUM_ENCODERS; i++) {
    pinMode(encoders[i].clkPin, INPUT_PULLUP);
    pinMode(encoders[i].dtPin,  INPUT_PULLUP);
    encState[i] = R_START;
    attachInterrupt(digitalPinToInterrupt(encoders[i].clkPin), readEncoders, CHANGE);
    attachInterrupt(digitalPinToInterrupt(encoders[i].dtPin),  readEncoders, CHANGE);
    pinMode(encoders[i].swPin, INPUT_PULLUP);
    swLastState[i]    = HIGH;
    swLastDebounce[i] = 0;
  }
#else
  for (int i = 0; i < NUM_POTS; i++) lastSent[i] = -1.0f;
#endif

  // Output toggle switch. Input-only pin, no pull needed (driven both ways).
  pinMode(SWITCH_PIN, INPUT);
}

// Debounced read of the output toggle. Reported independently of the knob mode,
// so it must run before the pot branch's early-return in knobsLoop().
static void switchLoop() {
  int raw = digitalRead(SWITCH_PIN) == HIGH ? 1 : 0;
  unsigned long now = millis();
  if (raw != swLastRead) {
    swLastRead    = raw;
    swReadChanged = now;
  }
  if (raw != swPos && (now - swReadChanged) > SWITCH_DEBOUNCE_MS) {
    swPos = raw;
    Serial.print("switch:"); Serial.println(swPos);
  }
}

void knobsLoop() {
  switchLoop();

#if USE_ENCODER
  unsigned long now = millis();

  for (int i = 0; i < activeEncoders; i++) {
    noInterrupts();
    int delta = encDelta[i];
    encDelta[i] = 0;
    interrupts();

    // While the switch is held (and for ROT_SUPPRESS_MS after it releases), drop
    // this encoder's rotation so a press that jostles the shaft can't move volume.
    if (digitalRead(encoders[i].swPin) == LOW)
      rotSuppressUntil[i] = now + ROT_SUPPRESS_MS;
    if (now < rotSuppressUntil[i])
      continue;

    // delta is already whole detents (the full-step decoder emits one per click).
    // Encoders report relative deltas, not an absolute level; the on-device gauge
    // is driven by the authoritative `vol:` echo the PC sends back (see
    // handleVolumeLine in mixer.ino). Do NOT call s_cb here — that feeds ±1.0 into
    // displayShowKnob, which would snap the gauge to 0%/100%.
    if (delta > 0) {
      for (int d = 0; d < delta; d++) {
        Serial.print(encoders[i].id); Serial.println(":up");
      }
    } else if (delta < 0) {
      for (int d = 0; d > delta; d--) {
        Serial.print(encoders[i].id); Serial.println(":down");
      }
    }
  }

  for (int i = 0; i < activeEncoders; i++) {
    uint8_t sw = digitalRead(encoders[i].swPin);
    if (sw == LOW && swLastState[i] == HIGH && (now - swLastDebounce[i]) > 50) {
      Serial.print(encoders[i].id); Serial.println(":press");
      swLastDebounce[i] = now;
    }
    swLastState[i] = sw;
  }

#else
  static unsigned long lastSample = 0;
  if (millis() - lastSample < 25) return;
  lastSample = millis();

  for (int i = 0; i < activePots; i++) {
    float val = analogRead(pots[i].pin) / 4095.0f;
    smoothed[i] = smoothed[i] * 0.85f + val * 0.15f;

    if (abs(smoothed[i] - lastSent[i]) >= 0.01f) {
      Serial.print(pots[i].id);
      Serial.print(":");
      Serial.println(smoothed[i], 2);
      lastSent[i] = smoothed[i];
      if (s_cb) s_cb(pots[i].id, smoothed[i]);
    }
  }
#endif
}
