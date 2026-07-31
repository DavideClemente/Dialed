#include "knobs.h"
#include "display.h"
#include "assignments.h"
#include "idlegif.h"
#include "version.h"

// Runtime-configurable: the PC pushes the user's setting via "cfg:idle:<ms>"
// (see handleConfigLine). Seeded to the previous hardcoded default for the
// window between boot and the first sync.
static unsigned long idleTimeoutMs = 3000;
static unsigned long lastKnobActivity = 0;
static bool isIdle = true;

// Buffer for lines arriving from the PC (icon lines are ~11 KB)
static char  inLine[12000];
static int   inPos = 0;

static void handleVolumeLine(const char* line) {
  if (strncmp(line, "vol:", 4) != 0) return;
  const char* rest = line + 4;                // "knob1:0.42"
  if (strncmp(rest, "knob", 4) != 0) return;
  const char* colon = strchr(rest, ':');
  if (!colon) return;
  int idx = atoi(rest + 4) - 1;
  if (idx < 0 || idx >= MAX_KNOBS) return;
  float v = atof(colon + 1);
  displayShowKnob(idx, v);
  lastKnobActivity = millis();
  isIdle = false;
}

static void handleMuteLine(const char* line) {
  if (strncmp(line, "mute:", 5) != 0) return;
  const char* rest = line + 5;                // "knob1:1"
  if (strncmp(rest, "knob", 4) != 0) return;
  const char* colon = strchr(rest, ':');
  if (!colon) return;
  int idx = atoi(rest + 4) - 1;
  if (idx < 0 || idx >= MAX_KNOBS) return;
  bool muted = atoi(colon + 1) != 0;
  displayShowMute(idx, muted);
  lastKnobActivity = millis();
  isIdle = false;
}

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

void readIncomingSerial() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n' || c == '\r') {
      if (inPos > 0) {
        inLine[inPos] = '\0';
        // GIF upload lines are the hot path during a transfer; handle them first
        // and skip the other parsers when consumed.
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
        inPos = 0;
      }
    } else if (inPos < (int)sizeof(inLine) - 1) {
      inLine[inPos++] = c;
    }
  }
}

void onKnobChange(const char* id, float value) {
  int idx = -1;
  if (strncmp(id, "knob", 4) == 0)
    idx = atoi(id + 4) - 1;   // "knob1" -> 0

  if (idx >= 0 && idx < MAX_KNOBS)
    displayShowKnob(idx, value);

  lastKnobActivity = millis();
  isIdle = false;
}

// The toggle moved. Draw the card immediately from stored assignment data — the
// PC's "out:" confirmation may never arrive (Dialed closed) and must not be
// waited on. Counts as activity, so the card dwells for cfg:idle:<ms> and then
// falls to the idle screen through the normal loop() check.
void onSwitchChange(int pos) {
  displayShowOutput(pos, OUT_PENDING);
  lastKnobActivity = millis();
  isIdle = false;
}

void setup() {
  displaySetup();
  knobsSetup(onKnobChange);   // knobsSetup calls Serial.begin(921600)
  knobsSetSwitchCallback(onSwitchChange);
  sendFirmwareVersion();      // announce firmware version to the PC on boot
  lastKnobActivity = millis();
}

void loop() {
  readIncomingSerial();
  knobsLoop();

  if (!isIdle && millis() - lastKnobActivity > idleTimeoutMs) {
    displayEnterIdle();
    isIdle = true;
  }

  displayTick();
}
